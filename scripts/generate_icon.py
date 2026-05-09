from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

from PIL import Image, ImageFilter


ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)


def color_distance(a: tuple[int, int, int, int], b: tuple[int, int, int, int]) -> int:
    return abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2])


def remove_corner_background(source: Image.Image, tolerance: int = 42) -> Image.Image:
    image = source.convert("RGBA")
    width, height = image.size
    pixels = image.load()

    corners = [
        pixels[0, 0],
        pixels[width - 1, 0],
        pixels[0, height - 1],
        pixels[width - 1, height - 1],
    ]
    opaque_corners = [corner for corner in corners if corner[3] > 245]
    if len(opaque_corners) < 3:
        return image

    # Treat only light, corner-connected backgrounds as removable. This avoids
    # deleting dark or colorful artwork that intentionally touches the edges.
    avg_luma = sum(0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2] for c in opaque_corners) / len(opaque_corners)
    if avg_luma < 210:
        return image

    visited = bytearray(width * height)
    queue: deque[tuple[int, int]] = deque()

    def enqueue_if_background(x: int, y: int) -> None:
        index = y * width + x
        if visited[index]:
            return
        pixel = pixels[x, y]
        if pixel[3] == 0:
            visited[index] = 1
            queue.append((x, y))
            return
        if pixel[3] > 245 and min(color_distance(pixel, c) for c in opaque_corners) <= tolerance:
            visited[index] = 1
            queue.append((x, y))

    for x in range(width):
        enqueue_if_background(x, 0)
        enqueue_if_background(x, height - 1)
    for y in range(height):
        enqueue_if_background(0, y)
        enqueue_if_background(width - 1, y)

    while queue:
        x, y = queue.popleft()
        pixels[x, y] = (pixels[x, y][0], pixels[x, y][1], pixels[x, y][2], 0)
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if 0 <= nx < width and 0 <= ny < height:
                enqueue_if_background(nx, ny)

    return image


def normalize_logo(source: Image.Image, output_size: int | None = None) -> Image.Image:
    image = remove_corner_background(source)
    alpha = image.getchannel("A")
    bbox = alpha.getbbox()
    if bbox is None:
        return image

    cropped = image.crop(bbox)
    side = max(cropped.width, cropped.height)
    padding = max(1, round(side * 0.04))
    side += padding * 2

    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.alpha_composite(cropped, ((side - cropped.width) // 2, (side - cropped.height) // 2))

    if output_size is not None and canvas.size != (output_size, output_size):
        canvas = canvas.resize((output_size, output_size), Image.Resampling.LANCZOS)
    return canvas


def fit_square(source: Image.Image, size: int) -> Image.Image:
    image = normalize_logo(source)
    image.thumbnail((size, size), Image.Resampling.LANCZOS)
    if size <= 32:
        image = image.filter(ImageFilter.UnsharpMask(radius=0.6, percent=120, threshold=2))

    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    x = (size - image.width) // 2
    y = (size - image.height) // 2
    canvas.alpha_composite(image, (x, y))
    return canvas


def generate_icon(input_path: Path, output_path: Path, normalized_png: Path | None) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(input_path) as source:
        if normalized_png is not None:
            normalized_png.parent.mkdir(parents=True, exist_ok=True)
            normalize_logo(source, output_size=1024).save(normalized_png)

        frames = [fit_square(source, size) for size in ICON_SIZES]
        frames[-1].save(
            output_path,
            format="ICO",
            sizes=[(size, size) for size in ICON_SIZES],
            append_images=frames[:-1],
        )


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate the CleanDesk Windows .ico file.")
    parser.add_argument(
        "--input",
        default="src/CleanDesk.App/Assets/CleanDesk_logo.png",
        help="Source PNG path.",
    )
    parser.add_argument(
        "--output",
        default="src/CleanDesk.App/Assets/CleanDesk.ico",
        help="Destination .ico path.",
    )
    parser.add_argument(
        "--write-normalized-png",
        default=None,
        help="Optional path for a transparent, cropped source PNG copy.",
    )
    args = parser.parse_args()

    generate_icon(
        Path(args.input),
        Path(args.output),
        Path(args.write_normalized_png) if args.write_normalized_png else None,
    )


if __name__ == "__main__":
    main()
