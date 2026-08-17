#!/usr/bin/env python3
"""測試 Overlay 功能是否能正確從 JSON 讀取並繪製 mask（輪廓版）"""

import json
import base64
from PIL import Image, ImageDraw
import io
import sys
import numpy as np
from pathlib import Path

def find_contours(mask_array, threshold=128):
    """
    找出 mask 的輪廓點
    使用邊緣檢測：檢查每個像素的 4 鄰域
    """
    height, width = mask_array.shape
    contour_points = []
    
    for y in range(1, height - 1):
        for x in range(1, width - 1):
            if mask_array[y, x] > threshold:
                # 檢查是否為邊緣（4鄰域）
                is_edge = (
                    mask_array[y-1, x] < threshold or  # 上
                    mask_array[y+1, x] < threshold or  # 下
                    mask_array[y, x-1] < threshold or  # 左
                    mask_array[y, x+1] < threshold     # 右
                )
                
                if is_edge:
                    contour_points.append((x, y))
    
    return contour_points

def test_overlay(jpg_path, json_path, output_path):
    """
    讀取 JPG 和對應的 JSON，將 mask 輪廓 overlay 到圖片上
    """
    # 1. 讀取原始圖片
    print(f"讀取圖片: {jpg_path}")
    img = Image.open(jpg_path).convert("RGB")
    
    # 2. 讀取 JSON
    print(f"讀取 JSON: {json_path}")
    with open(json_path, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    # 3. 解析 mask 數據
    if "raw_response" not in data:
        print("❌ JSON 中沒有 raw_response 欄位")
        return False
    
    raw_response = data["raw_response"]
    if "data" not in raw_response or "predictions" not in raw_response["data"]:
        print("❌ JSON 結構不正確")
        return False
    
    predictions = raw_response["data"]["predictions"]
    
    # 取得第一個預測結果
    first_pred = next(iter(predictions.values()))
    
    if "base64Image" not in first_pred:
        print("❌ 沒有 base64Image 欄位")
        return False
    
    base64_images = first_pred["base64Image"]
    
    print(f"找到 {len(base64_images)} 個 mask")
    
    # 建立繪圖物件
    draw = ImageDraw.Draw(img)
    
    # 4. 繪製每個 mask 的輪廓
    for idx, mask_data in enumerate(base64_images):
        class_name = mask_data.get("class", "unknown")
        mask_base64 = mask_data.get("mask", "")
        
        print(f"  處理 mask {idx+1}: {class_name}")
        
        try:
            # 解碼 base64 -> PNG bytes
            mask_bytes = base64.b64decode(mask_base64)
            
            # PNG bytes -> Image
            mask_img = Image.open(io.BytesIO(mask_bytes)).convert("L")  # 轉灰階
            
            # 縮放 mask 到原圖大小
            mask_img = mask_img.resize(img.size, Image.BILINEAR)
            
            # 轉換為 numpy 陣列以便處理
            mask_array = np.array(mask_img)
            
            # 找出輪廓點
            print(f"    提取輪廓...")
            contour_points = find_contours(mask_array)
            print(f"    找到 {len(contour_points)} 個輪廓點")
            
            # 繪製輪廓線（紫色，較粗的線條）
            color = (139, 92, 246)  # 紫色
            line_width = 3
            
            # 繪製每個輪廓點（使用小圓點使線條更連續）
            for x, y in contour_points:
                draw.ellipse([x-line_width, y-line_width, x+line_width, y+line_width], 
                           fill=color, outline=color)
            
            print(f"    ✓ 成功繪製輪廓")
            
        except Exception as e:
            print(f"    ❌ 失敗: {e}")
            import traceback
            traceback.print_exc()
            continue
    
    # 5. 儲存結果
    print(f"儲存結果: {output_path}")
    img.save(output_path, "JPEG", quality=95)
    
    return True

if __name__ == "__main__":
    # 測試一個檔案
    base_dir = Path("seg_test/result/dirt")
    test_file = "heat_sink_0_0_0_250728_crops_20250725182823.473_crop_11_copy_10"
    
    jpg_path = base_dir / f"{test_file}.jpg"
    json_path = base_dir / f"{test_file}.json"
    output_path = base_dir / f"{test_file}_overlay_test.jpg"
    
    if not jpg_path.exists():
        print(f"❌ 找不到檔案: {jpg_path}")
        sys.exit(1)
    
    if not json_path.exists():
        print(f"❌ 找不到檔案: {json_path}")
        sys.exit(1)
    
    print("=" * 60)
    print("開始測試 Overlay 功能")
    print("=" * 60)
    
    success = test_overlay(str(jpg_path), str(json_path), str(output_path))
    
    if success:
        print("\n✅ 測試成功！請檢查輸出檔案:")
        print(f"   {output_path}")
    else:
        print("\n❌ 測試失敗")
        sys.exit(1)

