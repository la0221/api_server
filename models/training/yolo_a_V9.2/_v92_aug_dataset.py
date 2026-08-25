# -*- coding: utf-8 -*-
"""V9.2 域隨機化(domain randomization)強外觀增強 dataset。
覆寫 mohao tier1 的 appearance_jitter → 大幅拉寬亮度/對比/gamma/CLAHE/模糊/噪聲，
逼模型學「外觀無關」。★ 不餵任何現場 M28 圖，測「純靠增強能否泛化到未見外觀」。"""
from __future__ import annotations
import sys, random
from pathlib import Path
import cv2, numpy as np
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
for p in (REPO/"OCR"/"yolo_a_V6", REPO/"OCR"/"yolo_a_V6.7", REPO/"OCR"/"yolo_a_V6.7.1", REPO/"OCR"/"yolo_a_V6.7.3"):
    sys.path.insert(0, str(p))
from v673_dataset import MohaoMixedTierDataset
from v671_aug_ops import jitter_gamma, gaussian_blur, gaussian_noise

def strong_jitter(roi, rng):
    """域隨機化：廣、強、非針對 M28。涵蓋『較亮較平』但不只針對它。"""
    # 亮度+對比（比原 appearance_jitter 的 0.6~1.4 / ±40 寬很多）
    if rng.random() < 0.9:
        alpha = rng.uniform(0.4, 1.8)      # 對比 0.4~1.8
        beta  = rng.uniform(-70, 70)       # 亮度 ±70
        roi = cv2.convertScaleAbs(roi, alpha=alpha, beta=beta)
    # gamma 0.5~1.8
    if rng.random() < 0.6:
        roi = jitter_gamma(roi, rng.uniform(0.5, 1.8))
    # 隨機 CLAHE（模擬各種對比正規化）
    if rng.random() < 0.45:
        clip = rng.uniform(1.0, 4.0)
        g = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
        g = cv2.createCLAHE(clipLimit=clip, tileGridSize=(8, 8)).apply(g)
        roi = cv2.cvtColor(g, cv2.COLOR_GRAY2BGR)
    # 銳/糊變化
    if rng.random() < 0.3:
        roi = gaussian_blur(roi, rng.choice([3, 5]))
    # 輕噪
    if rng.random() < 0.3:
        roi = gaussian_noise(roi, rng.uniform(3, 10))
    return roi

class MohaoDomainRandDataset(MohaoMixedTierDataset):
    def _tier1_jitter(self, roi, rng):
        return strong_jitter(roi, rng)
