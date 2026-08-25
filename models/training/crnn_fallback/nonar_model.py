"""非 autoregressive OCR — 4 個固定 query token、cross-attn 到 encoder、每 position 獨立分類。

跟 attn_model 的差異：
  - 沒有 SOS/EOS 教師強制、沒有 autoregressive
  - decoder 收固定 4 個 learnable query embedding、對 encoder 做 cross-attn
  - 每個 query 位置獨立輸出 12 類（blank + M + 0-9）
  - 標籤：對齊到 4 位、缺位填 blank（M60 → M,6,0,blank；M101 → M,1,0,1）

理論優勢：per-position 獨立、無 sequential LM prior、真正 open-vocab

Alphabet 同 CRNN：<blank>, M, 0-9 = 12 tokens
"""
from __future__ import annotations

import torch
import torch.nn as nn

ALPHABET = ["<blank>", "M", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"]
CHAR2IDX = {c: i for i, c in enumerate(ALPHABET)}
IDX2CHAR = {i: c for i, c in enumerate(ALPHABET)}
NUM_CLASSES = len(ALPHABET)  # 12
BLANK_ID = 0
N_QUERIES = 4  # max mohao 長度 = 4 (M101)


def encode_padded(s: str, n: int = N_QUERIES) -> list[int]:
    ids = [CHAR2IDX[c] for c in s]
    ids += [BLANK_ID] * (n - len(ids))
    return ids[:n]


def decode_padded(ids) -> str:
    """把 4-slot 預測還原成 mohao 字串（去掉尾 blank）。"""
    out = []
    for t in ids:
        t = int(t)
        if t == BLANK_ID:
            break  # 遇 blank 停（假設 blank 只出現在尾巴）
        if t in IDX2CHAR:
            out.append(IDX2CHAR[t])
    return "".join(out)


def _conv_bn_relu(cin, cout, k=3, s=1, p=1):
    return nn.Sequential(
        nn.Conv2d(cin, cout, k, s, p, bias=False),
        nn.BatchNorm2d(cout),
        nn.ReLU(inplace=True),
    )


class Encoder(nn.Module):
    def __init__(self, dim: int = 256):
        super().__init__()
        self.cnn = nn.Sequential(
            _conv_bn_relu(3, 32), nn.MaxPool2d(2),
            _conv_bn_relu(32, 64), nn.MaxPool2d(2),
            _conv_bn_relu(64, 128), _conv_bn_relu(128, 128), nn.MaxPool2d((2, 2)),
            _conv_bn_relu(128, 256), _conv_bn_relu(256, dim),
            nn.AdaptiveAvgPool2d((1, 25)),
        )

    def forward(self, x):
        return self.cnn(x)


class NonAROCR(nn.Module):
    def __init__(self, num_classes: int = NUM_CLASSES, dim: int = 256,
                 nheads: int = 4, nlayers: int = 2, enc_len: int = 25,
                 n_queries: int = N_QUERIES):
        super().__init__()
        self.encoder = Encoder(dim)
        self.enc_pos = nn.Parameter(torch.randn(1, enc_len, dim) * 0.02)
        self.queries = nn.Parameter(torch.randn(1, n_queries, dim) * 0.02)
        layer = nn.TransformerDecoderLayer(
            dim, nheads, 4 * dim, dropout=0.1, batch_first=True, norm_first=True)
        self.decoder = nn.TransformerDecoder(layer, num_layers=nlayers)
        self.head = nn.Linear(dim, num_classes)
        self.n_queries = n_queries
        self.enc_len = enc_len

    def encode(self, x):
        f = self.encoder(x)                       # (B, dim, 1, enc_len)
        f = f.squeeze(2).transpose(1, 2)          # (B, enc_len, dim)
        return f + self.enc_pos

    def forward(self, x):
        """輸出 (B, n_queries, num_classes) 每 position 獨立 logits。"""
        memory = self.encode(x)
        B = memory.shape[0]
        q = self.queries.expand(B, -1, -1)
        # 不加 causal mask、tgt_mask=None → queries 之間可以互看，但無「先後」限制
        out = self.decoder(q, memory)
        return self.head(out)
