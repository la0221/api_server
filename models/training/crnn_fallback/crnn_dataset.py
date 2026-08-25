"""CRNN dataset — polar strip → 字串標籤 for CTC training.

沿用 V6.7 annulus_polar 前處理（避免與現有分類器 pipeline 分歧）；
差別在標籤：分類器用 class index，CRNN 用字元序列。
"""
from __future__ import annotations

import math
import random
import sys
from pathlib import Path
from typing import Iterable

import cv2
import numpy as np
import torch
from torch.utils.data import Dataset

cv2.setNumThreads(0)  # Windows cv2 + DataLoader deadlock 修法

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa: E402

PAD_VALUE = 255
R_INNER = 0.6  # 與 V6.7 一致，不可改

# strip 產出是 640×640 但文字帶只在 rows [299, ~340]。
# 這對所有模號穩定（band = 0.4*640/(2π) ≈ 40 pixels 高，白 pad 置中）。
# 訓練/推論只送文字帶：VRAM 8×減、compute 8×快、模型看到的訊號更集中。
BAND_TOP = 280
BAND_BOTTOM = 360
BAND_H = BAND_BOTTOM - BAND_TOP  # 80


def crop_band(strip: np.ndarray) -> np.ndarray:
    """640×640 strip → 80×640 文字帶。"""
    return strip[BAND_TOP:BAND_BOTTOM]

# 字元字典：blank(CTC 專用) + M + 0-9 = 12
ALPHABET = ["<blank>", "M", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"]
CHAR2IDX = {c: i for i, c in enumerate(ALPHABET)}
IDX2CHAR = {i: c for i, c in enumerate(ALPHABET)}
NUM_CLASSES = len(ALPHABET)  # 12


def encode_label(s: str) -> list[int]:
    return [CHAR2IDX[c] for c in s]


def decode_greedy(logits: np.ndarray) -> str:
    """CTC greedy decode: argmax → dedup → strip blank."""
    idx = logits.argmax(-1)
    out = []
    prev = -1
    for i in idx:
        if i != prev and i != 0:
            out.append(IDX2CHAR[int(i)])
        prev = i
    return "".join(out)


def annulus_polar(roi: np.ndarray, do_rotate: bool, size: int, r_inner: float = R_INNER) -> np.ndarray:
    """複製自 V6.7 v67_dataset.annulus_polar，避開 v6_dataset 相依。"""
    h, w = roi.shape[:2]
    cx, cy = w // 2, h // 2
    r = min(cx, cy)
    if do_rotate:
        ang = random.uniform(0.0, 360.0)
        M = cv2.getRotationMatrix2D((cx, cy), ang, 1.0)
        roi = cv2.warpAffine(roi, M, (w, h), flags=cv2.INTER_LINEAR,
                             borderValue=(PAD_VALUE,) * 3)
    C = 2 * math.pi * r
    pol = cv2.warpPolar(roi, (int(r), int(C)), (cx, cy), r,
                        cv2.INTER_LINEAR + cv2.WARP_POLAR_LINEAR)
    r0 = int(r_inner * r)
    pol = pol[:, r0:]
    return white_pad_square(cv2.transpose(cv2.flip(pol, 1)), size)


def preprocess_for_crnn(img: np.ndarray, do_rotate: bool, size: int = 640) -> np.ndarray:
    """raw 圖 → 640×640 polar strip。Hough fail → 直接 pad 原圖（與 ocr_demo 一致）。"""
    circ = find_circle(img)
    if circ is None:
        return white_pad_square(img, size)
    cx, cy, r = circ
    x0, y0 = max(0, cx - r), max(0, cy - r)
    x1, y1 = min(img.shape[1], cx + r), min(img.shape[0], cy + r)
    roi = white_pad_square(img[y0:y1, x0:x1], target=2 * r)
    return annulus_polar(roi, do_rotate, size)


def to_tensor(strip: np.ndarray) -> torch.Tensor:
    """HWC uint8 → CHW float [-1, 1]"""
    x = strip.astype(np.float32) / 255.0
    x = x.transpose(2, 0, 1)
    x = (x - 0.5) / 0.5
    return torch.from_numpy(x)


class MohaoCRNNDataset(Dataset):
    """讀 data_v671/mohao/{split}/{mold}/*.jpg → (strip_tensor, label_indices)。

    exclude: 要 hold out 的模號（e.g. ["M54"]）
    skip_ng: 是否跳過 NG 類（CRNN 不學 NG，靠信心閾值判定）
    """
    def __init__(self, root: Path, split: str, exclude: Iterable[str] = (),
                 skip_ng: bool = True, do_rotate: bool = False, size: int = 640):
        self.size = size
        self.do_rotate = do_rotate
        self.samples: list[tuple[Path, str]] = []
        exclude_set = set(exclude)
        split_dir = Path(root) / split
        for cls_dir in sorted(split_dir.iterdir()):
            mold_id = cls_dir.name
            if mold_id in exclude_set:
                continue
            if skip_ng and mold_id == "NG":
                continue
            if not all(c in CHAR2IDX for c in mold_id):
                # 跳過字元集外的類（不該發生，但保險）
                continue
            for p in cls_dir.iterdir():
                if p.suffix.lower() in {".jpg", ".jpeg", ".png"}:
                    self.samples.append((p, mold_id))

    def __len__(self) -> int:
        return len(self.samples)

    def __getitem__(self, i: int):
        path, label_str = self.samples[i]
        img = imread_unicode(path)
        strip = preprocess_for_crnn(img, self.do_rotate, self.size)
        x = to_tensor(strip)
        y = torch.tensor(encode_label(label_str), dtype=torch.long)
        return x, y, label_str


class StripDataset(Dataset):
    """快取版：讀已 pre-processed 的 640×640 strip PNG，跳過 Hough+warpPolar。

    訓練時支援水平環狀 shift 增強（=極座標下的 Cartesian rotation，因為 warpPolar 已把角度
    映到 x 軸；直接 np.roll 更快、不需重跑 warpPolar）。

    exclude: 排除的模號類（M54 holdout）
    aug_hshift: 訓練 True，val/test False
    """
    def __init__(self, root: Path, split: str, exclude: Iterable[str] = (),
                 skip_ng: bool = True, aug_hshift: bool = False, size: int = 640):
        self.size = size
        self.aug_hshift = aug_hshift
        self.samples: list[tuple[Path, str]] = []
        exclude_set = set(exclude)
        split_dir = Path(root) / split
        for cls_dir in sorted(split_dir.iterdir()):
            if not cls_dir.is_dir():
                continue
            mold_id = cls_dir.name
            if mold_id in exclude_set:
                continue
            if skip_ng and mold_id == "NG":
                continue
            if not all(c in CHAR2IDX for c in mold_id):
                continue
            for p in cls_dir.iterdir():
                if p.suffix.lower() == ".png":
                    self.samples.append((p, mold_id))

    def __len__(self) -> int:
        return len(self.samples)

    def __getitem__(self, i: int):
        path, label_str = self.samples[i]
        strip = imread_unicode(path)  # 已是 640×640 strip
        strip = crop_band(strip)      # → 80×640 文字帶
        if self.aug_hshift:
            shift = random.randint(0, self.size - 1)
            strip = np.roll(strip, shift, axis=1)
        x = to_tensor(strip)
        y = torch.tensor(encode_label(label_str), dtype=torch.long)
        return x, y, label_str


class CropDataset(Dataset):
    """讀 data_v671_crops_v2/{split}/{label}/*.png — 80×200 detector-based crops。

    label 是資料夾名（模號 "M101" 或穴號 "05"），alphabet 通用 {blank, M, 0-9}。
    exclude 排除 mohao holdout（e.g. ["M54"]）。訓練時 hshift 小幅平移增強魯棒。
    """
    def __init__(self, root: Path, split: str, exclude: Iterable[str] = (),
                 aug_shift: int = 0):
        self.aug_shift = aug_shift
        self.samples: list[tuple[Path, str]] = []
        exclude_set = set(exclude)
        split_dir = Path(root) / split
        for cls_dir in sorted(split_dir.iterdir()):
            if not cls_dir.is_dir() or cls_dir.name in exclude_set:
                continue
            label = cls_dir.name
            if not all(c in CHAR2IDX for c in label):
                continue
            for p in cls_dir.iterdir():
                if p.suffix.lower() == ".png":
                    self.samples.append((p, label))

    def __len__(self) -> int:
        return len(self.samples)

    def __getitem__(self, i: int):
        path, label_str = self.samples[i]
        crop = imread_unicode(path)  # 80×200
        if self.aug_shift > 0:
            s = random.randint(-self.aug_shift, self.aug_shift)
            if s != 0:
                crop = np.roll(crop, s, axis=1)
        x = to_tensor(crop)
        y = torch.tensor(encode_label(label_str), dtype=torch.long)
        return x, y, label_str


class FlatStripDataset(Dataset):
    """遞迴讀單一資料夾（跨模號多批），全部 label 都是同一個 label_str。
    用於 eval 的 cross-source holdout 場景（e.g. 前處理區/M54/**/*.png）。"""
    def __init__(self, root: Path, label_str: str, pattern: str = "*.png"):
        self.label_str = label_str
        self.samples = sorted(p for p in Path(root).rglob(pattern)
                              if p.is_file())

    def __len__(self) -> int:
        return len(self.samples)

    def __getitem__(self, i: int):
        p = self.samples[i]
        strip = imread_unicode(p)
        if strip.shape[:2] != (640, 640):
            strip = white_pad_square(strip, 640)
        strip = crop_band(strip)
        x = to_tensor(strip)
        y = torch.tensor(encode_label(self.label_str), dtype=torch.long)
        return x, y, self.label_str


def collate_ctc(batch):
    """把不同長度 label 拼成 (concat_labels, label_lengths) for CTC。"""
    xs, ys, ss = zip(*batch)
    xs = torch.stack(xs, 0)
    label_lengths = torch.tensor([len(y) for y in ys], dtype=torch.long)
    labels = torch.cat(ys, 0)
    return xs, labels, label_lengths, list(ss)
