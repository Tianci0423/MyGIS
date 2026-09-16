import argparse
import math
import os
import uuid

import numpy as np
import rasterio
from rasterio.crs import CRS
from rasterio.enums import MaskFlags, Resampling
from rasterio.vrt import WarpedVRT
from rasterio.warp import calculate_default_transform
from rasterio.windows import Window


RESAMPLING_METHODS = {
    "nearest": Resampling.nearest,
    "bilinear": Resampling.bilinear,
    "cubic": Resampling.cubic,
    "lanczos": Resampling.lanczos,
}

WINDOW_SIZE = 1024


def parse_crs(value):
    text = value.strip()
    if text.isdigit():
        text = f"EPSG:{text}"
    try:
        return CRS.from_user_input(text)
    except Exception as exc:
        raise ValueError(f"无法识别目标坐标系：{value}") from exc


def iter_windows(width, height):
    for row_off in range(0, height, WINDOW_SIZE):
        window_height = min(WINDOW_SIZE, height - row_off)
        for col_off in range(0, width, WINDOW_SIZE):
            window_width = min(WINDOW_SIZE, width - col_off)
            yield Window(col_off, row_off, window_width, window_height)


def copy_metadata(src, dst, resampling):
    default_tags = src.tags()
    if default_tags:
        dst.update_tags(**default_tags)

    for band_index in range(1, src.count + 1):
        band_tags = src.tags(band_index)
        if band_tags:
            dst.update_tags(band_index, **band_tags)

    try:
        dst.descriptions = src.descriptions
    except Exception:
        pass
    try:
        dst.units = src.units
    except Exception:
        pass
    try:
        dst.scales = src.scales
        dst.offsets = src.offsets
    except Exception:
        pass
    try:
        dst.colorinterp = src.colorinterp
    except Exception:
        pass

    if src.count == 1 and resampling == Resampling.nearest:
        try:
            color_map = src.colormap(1)
            if color_map:
                dst.write_colormap(1, color_map)
        except Exception:
            pass


def reproject_raster(input_path, output_path, target_crs, resampling, resolution):
    output_path = os.path.abspath(output_path)
    output_dir = os.path.dirname(output_path)
    os.makedirs(output_dir, exist_ok=True)

    token = uuid.uuid4().hex
    temp_path = os.path.join(
        output_dir,
        f".{os.path.basename(output_path)}.{token}.tmp.tif",
    )
    temp_mask_path = temp_path + ".msk"
    output_mask_path = output_path + ".msk"

    try:
        with rasterio.open(input_path) as src:
            if src.crs is None:
                raise ValueError("输入影像没有坐标系，无法执行重投影")
            if src.transform is None:
                raise ValueError("输入影像没有有效的地理变换参数")

            transform_kwargs = {}
            if resolution is not None:
                transform_kwargs["resolution"] = resolution

            transform, width, height = calculate_default_transform(
                src.crs,
                target_crs,
                src.width,
                src.height,
                *src.bounds,
                **transform_kwargs,
            )
            width = int(width)
            height = int(height)
            if width <= 0 or height <= 0:
                raise ValueError(f"目标网格尺寸无效：{width}×{height}")

            bytes_per_sample = np.dtype(src.dtypes[0]).itemsize
            estimated_bytes = width * height * src.count * bytes_per_sample
            if estimated_bytes > 2 * 1024**4:
                raise ValueError(
                    "目标分辨率会生成超过 2 TiB 的影像，请增大目标像元大小"
                )

            profile = src.profile.copy()
            profile.update(
                driver="GTiff",
                crs=target_crs,
                transform=transform,
                width=width,
                height=height,
                tiled=True,
                blockxsize=512,
                blockysize=512,
                compress="deflate",
                predictor=3 if np.issubdtype(np.dtype(src.dtypes[0]), np.floating) else 2,
                BIGTIFF="IF_SAFER",
            )

            vrt_options = {
                "crs": target_crs,
                "transform": transform,
                "width": width,
                "height": height,
                "resampling": resampling,
                "warp_mem_limit": 512,
            }
            if src.nodata is not None:
                vrt_options["src_nodata"] = src.nodata
                vrt_options["nodata"] = src.nodata

            needs_dataset_mask = any(
                MaskFlags.all_valid not in band_flags
                and MaskFlags.alpha not in band_flags
                and MaskFlags.nodata not in band_flags
                for band_flags in src.mask_flag_enums
            )

            print(f"Source CRS: {src.crs}", flush=True)
            print(f"Target CRS: {target_crs}", flush=True)
            print(f"Target grid: {width}x{height}", flush=True)

            total_pixels = width * height
            completed_pixels = 0
            last_percent = -1

            with WarpedVRT(src, **vrt_options) as vrt:
                with rasterio.open(temp_path, "w", **profile) as dst:
                    copy_metadata(src, dst, resampling)
                    for window in iter_windows(width, height):
                        data = vrt.read(window=window)
                        dst.write(data, window=window)
                        if needs_dataset_mask:
                            dst.write_mask(vrt.dataset_mask(window=window), window=window)

                        completed_pixels += int(window.width * window.height)
                        percent = min(100, int(completed_pixels * 100 / total_pixels))
                        if percent > last_percent:
                            print(f"Reproject: {percent}%", flush=True)
                            last_percent = percent

            if last_percent < 100:
                print("Reproject: 100%", flush=True)

        if os.path.exists(output_path):
            os.remove(output_path)
        if os.path.exists(output_mask_path):
            os.remove(output_mask_path)
        os.replace(temp_path, output_path)
        if os.path.exists(temp_mask_path):
            os.replace(temp_mask_path, output_mask_path)
    except Exception:
        for path in (temp_path, temp_mask_path):
            try:
                if os.path.exists(path):
                    os.remove(path)
            except OSError:
                pass
        raise


def main():
    parser = argparse.ArgumentParser(description="GeoVision raster reprojection")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--target-crs", required=True)
    parser.add_argument(
        "--resampling",
        choices=tuple(RESAMPLING_METHODS),
        default="bilinear",
    )
    parser.add_argument(
        "--resolution",
        type=float,
        help="Optional square target pixel size in target CRS units",
    )
    args = parser.parse_args()

    input_path = os.path.abspath(args.input)
    output_path = os.path.abspath(args.output)
    if input_path.lower() == output_path.lower():
        raise ValueError("输出文件不能覆盖输入影像")

    if args.resolution is not None and (
        not math.isfinite(args.resolution) or args.resolution <= 0
    ):
        raise ValueError("目标像元大小必须是大于 0 的有限数字")

    target_crs = parse_crs(args.target_crs)
    resampling = RESAMPLING_METHODS[args.resampling]
    reproject_raster(
        input_path,
        output_path,
        target_crs,
        resampling,
        args.resolution,
    )
    print(f"Saved to: {os.path.abspath(args.output)}", flush=True)


if __name__ == "__main__":
    main()
