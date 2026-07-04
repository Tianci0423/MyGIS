import argparse
import json
import os

import rasterio
from rasterio.mask import mask
from rasterio.windows import Window, from_bounds
from rasterio.warp import transform_geom


def clip_by_extent(src, output_path, bounds):
    window = from_bounds(*bounds, transform=src.transform)
    window = window.round_offsets().round_lengths()
    full = Window(0, 0, src.width, src.height)
    window = window.intersection(full)
    if window.width <= 0 or window.height <= 0:
        raise ValueError("裁剪范围与影像没有重叠区域")

    data = src.read(window=window)
    profile = src.profile.copy()
    profile.update(
        width=int(window.width),
        height=int(window.height),
        transform=src.window_transform(window),
        compress="deflate",
    )
    write_output(output_path, profile, data)


def clip_by_cutline(src, output_path, cutline_path):
    with open(cutline_path, "r", encoding="utf-8") as stream:
        payload = json.load(stream)

    geometries = payload.get("geometries") or []
    if not geometries:
        raise ValueError("矢量图层中没有可用于裁剪的面要素")

    vector_crs = payload.get("crs")
    if vector_crs and src.crs and str(src.crs) != vector_crs:
        geometries = [
            transform_geom(vector_crs, src.crs, geometry)
            for geometry in geometries
        ]

    data, transform = mask(src, geometries, crop=True)
    profile = src.profile.copy()
    profile.update(
        width=data.shape[2],
        height=data.shape[1],
        transform=transform,
        compress="deflate",
    )
    write_output(output_path, profile, data)


def write_output(output_path, profile, data):
    output_dir = os.path.dirname(os.path.abspath(output_path))
    os.makedirs(output_dir, exist_ok=True)
    with rasterio.open(output_path, "w", **profile) as dst:
        dst.write(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--mode", choices=("extent", "cutline"), required=True)
    parser.add_argument("--bounds", nargs=4, type=float)
    parser.add_argument("--cutline-json")
    args = parser.parse_args()

    with rasterio.open(args.input) as src:
        if args.mode == "extent":
            if not args.bounds:
                raise ValueError("范围裁剪缺少 bounds 参数")
            clip_by_extent(src, args.output, args.bounds)
        else:
            if not args.cutline_json:
                raise ValueError("矢量裁剪缺少 cutline-json 参数")
            clip_by_cutline(src, args.output, args.cutline_json)

    print(f"Saved to: {args.output}")


if __name__ == "__main__":
    main()
