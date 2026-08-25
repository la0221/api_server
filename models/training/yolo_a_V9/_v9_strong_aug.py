# -*- coding: utf-8 -*-
"""v9 強化(域隨機化)增強 dataset：mohao + xuehao 兩 head。
在【完整聯集】的真實外觀多樣性上，再加強外觀擾動 → 對未見機台外觀更穩。"""
from __future__ import annotations
import sys
from pathlib import Path
import cv2
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
for p in (REPO/"OCR"/"yolo_a_V6", REPO/"OCR"/"yolo_a_V6.7", REPO/"OCR"/"yolo_a_V6.7.1", REPO/"OCR"/"yolo_a_V6.7.3"):
    sys.path.insert(0, str(p))
from v673_dataset import MohaoMixedTierDataset, XuehaoMixedTierDataset
from v671_aug_ops import jitter_gamma, gaussian_blur, gaussian_noise

def strong_jitter(roi, rng):
    if rng.random() < 0.9:
        roi = cv2.convertScaleAbs(roi, alpha=rng.uniform(0.4, 1.8), beta=rng.uniform(-70, 70))
    if rng.random() < 0.6:
        roi = jitter_gamma(roi, rng.uniform(0.5, 1.8))
    if rng.random() < 0.45:
        clip = rng.uniform(1.0, 4.0)
        g = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
        g = cv2.createCLAHE(clipLimit=clip, tileGridSize=(8, 8)).apply(g)
        roi = cv2.cvtColor(g, cv2.COLOR_GRAY2BGR)
    if rng.random() < 0.3:
        roi = gaussian_blur(roi, rng.choice([3, 5]))
    if rng.random() < 0.3:
        roi = gaussian_noise(roi, rng.uniform(3, 10))
    return roi

class MohaoStrongDataset(MohaoMixedTierDataset):
    def _tier1_jitter(self, roi, rng): return strong_jitter(roi, rng)

class XuehaoStrongDataset(XuehaoMixedTierDataset):
    # 穴號原本 tier1 不抖動；強化版加上 strong_jitter（外觀擾動不影響穴號字形/朝向）
    def _tier1_jitter(self, roi, rng): return strong_jitter(roi, rng)
