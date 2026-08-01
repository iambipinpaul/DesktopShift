"""Generate every DesktopShift package icon from the canonical shift mark.

Run with Python 3 and Pillow installed. The outputs intentionally keep the
mark inside Windows' recommended safe area while the ICO uses the full canvas
for legibility at taskbar and tray sizes.
"""

from pathlib import Path

from PIL import Image, ImageDraw


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
ASSET_DIRECTORY = REPOSITORY_ROOT / "src" / "DesktopShift.App" / "Assets"
MASTER_SIZE = 1024
DESIGN_SIZE = 256


def scaled_points(points: list[tuple[int, int]], scale: float) -> list[tuple[float, float]]:
    return [(x * scale, y * scale) for x, y in points]


def render_shift_mark(size: int = MASTER_SIZE) -> Image.Image:
    scale = size / DESIGN_SIZE
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image, "RGBA")

    draw.rounded_rectangle(
        scaled_points([(12, 12), (244, 244)], scale),
        radius=54 * scale,
        fill=(37, 99, 235, 255),
    )
    draw.polygon(
        scaled_points(
            [(52, 70), (146, 70), (182, 106), (146, 142), (88, 142), (118, 112), (52, 112)]
            , scale
        ),
        fill=(255, 255, 255, 255),
    )
    draw.polygon(
        scaled_points(
            [(204, 186), (110, 186), (74, 150), (110, 114), (168, 114), (138, 144), (204, 144)]
            , scale
        ),
        fill=(191, 219, 254, 255),
    )
    draw.polygon(
        scaled_points([(52, 70), (94, 70), (124, 100), (82, 100)], scale),
        fill=(219, 234, 254, 204),
    )
    draw.polygon(
        scaled_points([(204, 186), (162, 186), (132, 156), (174, 156)], scale),
        fill=(255, 255, 255, 199),
    )
    return image


def compose_canvas(width: int, height: int, mark_size: int) -> Image.Image:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    mark = render_shift_mark().resize((mark_size, mark_size), Image.Resampling.LANCZOS)
    canvas.alpha_composite(mark, ((width - mark_size) // 2, (height - mark_size) // 2))
    return canvas


def save_png(name: str, width: int, height: int, mark_size: int) -> None:
    compose_canvas(width, height, mark_size).save(
        ASSET_DIRECTORY / name,
        format="PNG",
        optimize=True,
    )


def main() -> None:
    ASSET_DIRECTORY.mkdir(parents=True, exist_ok=True)

    save_png("LockScreenLogo.scale-200.png", 48, 48, 38)
    save_png("SplashScreen.scale-200.png", 1240, 600, 164)
    save_png("Square150x150Logo.scale-200.png", 300, 300, 220)
    save_png("Square44x44Logo.scale-200.png", 88, 88, 72)
    save_png("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24, 24)
    save_png("StoreLogo.png", 50, 50, 42)
    save_png("Wide310x150Logo.scale-200.png", 620, 300, 122)

    icon = render_shift_mark(256)
    icon.save(
        ASSET_DIRECTORY / "AppIcon.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
