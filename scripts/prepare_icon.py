from pathlib import Path

from PIL import Image


root = Path(__file__).resolve().parents[1]
source = root / "src/CalorieJournal.Desktop/Assets/app-icon.png"
target = source.with_suffix(".ico")
sizes = [(value, value) for value in (16, 24, 32, 48, 64, 128, 256)]

with Image.open(source) as image:
    if image.width != image.height or image.mode != "RGBA":
        raise ValueError("图标原图须为正方形RGBA图像。")
    if image.getchannel("A").getextrema() != (0, 255):
        raise ValueError("图标原图缺少透明外部或实色主体。")
    # 仅导出Windows多尺寸图标，原始图像及其透明通道保持不变。
    image.save(target, format="ICO", sizes=sizes)

with Image.open(target) as icon:
    if icon.info["sizes"] != set(sizes):
        raise ValueError("ICO尺寸不完整。")
    for size in sizes:
        frame = icon.ico.getimage(size)
        if frame.mode != "RGBA" or frame.getchannel("A").getextrema() != (0, 255):
            raise ValueError(f"ICO透明通道无效：{size}")
print("ICO各尺寸可读取且保留透明通道：", sizes)
