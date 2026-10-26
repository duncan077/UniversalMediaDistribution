from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ANDROID_RESOURCES = (
    ROOT / "src" / "UniversalMediaDistribution.Android" / "Resources"
)
FONT_BOLD = Path("C:/Windows/Fonts/segoeuib.ttf")
FONT_REGULAR = Path("C:/Windows/Fonts/segoeui.ttf")


def font(path: Path, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(path), size)


def gradient(size: tuple[int, int]) -> Image.Image:
    width, height = size
    image = Image.new("RGB", size)
    pixels = image.load()
    for y in range(height):
        for x in range(width):
            blend = (x / max(1, width - 1)) * 0.65 + (
                y / max(1, height - 1)
            ) * 0.35
            pixels[x, y] = (
                int(13 + 20 * blend),
                int(20 + 32 * blend),
                int(34 + 52 * blend),
            )
    return image


def create_tv_banner(
    resource_directory: str,
    top_line: str,
    middle_line: str,
    bottom_line: str,
) -> None:
    destination = (
        ANDROID_RESOURCES / resource_directory / "tv_banner.png"
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    image = gradient((320, 180))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(
        (18, 20, 105, 107),
        radius=20,
        fill=(37, 65, 96),
        outline=(101, 214, 173),
        width=4,
    )
    draw.text(
        (61, 63),
        "U",
        anchor="mm",
        font=font(FONT_BOLD, 55),
        fill=(245, 247, 255),
    )
    draw.text(
        (121, 32),
        top_line,
        font=font(FONT_BOLD, 20),
        fill=(245, 247, 255),
    )
    draw.text(
        (121, 62),
        middle_line,
        font=font(FONT_BOLD, 27),
        fill=(101, 214, 173),
    )
    draw.text(
        (22, 129),
        bottom_line,
        font=font(FONT_BOLD, 25),
        fill=(199, 211, 237),
    )
    image.save(destination, optimize=True)


def create_launcher_icon() -> None:
    destination = ANDROID_RESOURCES / "mipmap-xhdpi" / "ic_launcher.png"
    destination.parent.mkdir(parents=True, exist_ok=True)
    image = gradient((192, 192))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(
        (20, 20, 172, 172),
        radius=38,
        fill=(37, 65, 96),
        outline=(101, 214, 173),
        width=7,
    )
    draw.text(
        (96, 91),
        "U",
        anchor="mm",
        font=font(FONT_BOLD, 100),
        fill=(245, 247, 255),
    )
    draw.ellipse((134, 133, 158, 157), fill=(101, 214, 173))
    image.save(destination, optimize=True)


if __name__ == "__main__":
    create_tv_banner(
        "drawable-xhdpi",
        "UNIVERSAL",
        "MEDIA",
        "DISTRIBUTION",
    )
    create_tv_banner(
        "drawable-es-xhdpi",
        "DISTRIBUCIÓN",
        "MULTIMEDIA",
        "UNIVERSAL",
    )
    create_launcher_icon()
