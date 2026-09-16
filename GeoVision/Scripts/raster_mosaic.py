#!/usr/bin/env python3
"""Georeferenced two-image mosaicking with overlap color balancing and feathering."""

from __future__ import annotations

import argparse
import math
import os
import shutil
import sys
import tempfile
from contextlib import ExitStack

import numpy as np
import rasterio
from rasterio.enums import ColorInterp, Resampling
from rasterio.transform import from_origin
from rasterio.vrt import WarpedVRT
from rasterio.warp import transform_bounds
from rasterio.windows import Window, from_bounds
from scipy.ndimage import distance_transform_edt


RESAMPLING = {
    "nearest": Resampling.nearest,
    "bilinear": Resampling.bilinear,
    "cubic": Resampling.cubic,
    "lanczos": Resampling.lanczos,
}

QUANTILE_LEVELS = np.array(
    [0.0, 0.01, 0.05, 0.10, 0.20, 0.35, 0.50, 0.65, 0.80, 0.90, 0.95, 0.99, 1.0],
    dtype=np.float64,
)
MAX_FEATHER_DISTANCE = 1024
MAX_OUTPUT_BYTES = 2 * 1024**4


def progress(percent: int, label: str) -> None:
    print(f"Mosaic: {max(0, min(100, percent))}% {label}", flush=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, help="Base/reference GeoTIFF")
    parser.add_argument("--overlay", required=True, help="Second GeoTIFF")
    parser.add_argument("--output", required=True, help="Output GeoTIFF")
    parser.add_argument(
        "--color-balance",
        choices=("none", "linear", "histogram"),
        default="histogram",
    )
    parser.add_argument("--feather-distance", type=int, default=128)
    parser.add_argument("--resampling", choices=tuple(RESAMPLING), default="cubic")
    return parser.parse_args()


def ensure_inputs(base: rasterio.DatasetReader, overlay: rasterio.DatasetReader) -> None:
    if base.crs is None or overlay.crs is None:
        raise ValueError("两幅输入影像都必须包含有效的坐标系。")
    if base.transform.is_identity or overlay.transform.is_identity:
        raise ValueError("两幅输入影像都必须包含有效的地理变换。")
    if base.count != overlay.count:
        raise ValueError(
            f"两幅影像的波段数必须一致（当前为 {base.count} 和 {overlay.count}）。"
        )
    if base.count < 1:
        raise ValueError("输入影像不包含可用波段。")
    if base.dtypes != overlay.dtypes:
        raise ValueError(
            f"两幅影像的数据类型必须一致（当前为 {base.dtypes} 和 {overlay.dtypes}）。"
        )
    if not np.allclose(
        [base.transform.b, base.transform.d, overlay.transform.b, overlay.transform.d],
        0.0,
        atol=1e-12,
    ):
        raise ValueError("当前拼接输出采用北向上网格，暂不支持带旋转或倾斜参数的输入影像；请先重投影到北向上网格。")


def estimate_output_bytes(width: int, height: int, count: int, dtype: str) -> int:
    try:
        bytes_per_sample = np.dtype(dtype).itemsize
    except TypeError as exc:
        raise ValueError(f"无法识别输出数据类型：{dtype}") from exc
    raw_bytes = int(width) * int(height) * int(count) * int(bytes_per_sample)
    # Account for mask, TIFF structure and temporary file while writing.
    estimated = raw_bytes + raw_bytes // 5 + 128 * 1024 * 1024
    if estimated > MAX_OUTPUT_BYTES:
        raise ValueError(
            f"预计输出文件至少需要 {estimated / 1024**4:.2f} TiB，超过安全上限 2 TiB。"
            "请缩小范围或先降低分辨率。"
        )
    return estimated


def ensure_output_space(output_dir: str, estimated_bytes: int) -> None:
    try:
        free_bytes = shutil.disk_usage(output_dir).free
    except OSError as exc:
        raise ValueError(f"无法读取输出磁盘空间：{output_dir}") from exc
    if free_bytes < estimated_bytes:
        raise OSError(
            f"输出磁盘空间不足：预计需要 {estimated_bytes / 1024**3:.1f} GiB，"
            f"当前可用 {free_bytes / 1024**3:.1f} GiB。"
        )


def grid_for_union(base: rasterio.DatasetReader, overlay: rasterio.DatasetReader):
    overlay_bounds = transform_bounds(
        overlay.crs, base.crs, *overlay.bounds, densify_pts=21
    )
    left = min(base.bounds.left, overlay_bounds[0])
    bottom = min(base.bounds.bottom, overlay_bounds[1])
    right = max(base.bounds.right, overlay_bounds[2])
    top = max(base.bounds.top, overlay_bounds[3])

    resolution_x = math.hypot(base.transform.a, base.transform.d)
    resolution_y = math.hypot(base.transform.b, base.transform.e)
    if not all(math.isfinite(v) and v > 0 for v in (resolution_x, resolution_y)):
        raise ValueError("基准影像的像元大小无效。")

    left = math.floor(left / resolution_x) * resolution_x
    right = math.ceil(right / resolution_x) * resolution_x
    bottom = math.floor(bottom / resolution_y) * resolution_y
    top = math.ceil(top / resolution_y) * resolution_y
    width = max(1, int(round((right - left) / resolution_x)))
    height = max(1, int(round((top - bottom) / resolution_y)))
    transform = from_origin(left, top, resolution_x, resolution_y)
    return transform, width, height, (left, bottom, right, top)


def intersection_window(base, overlay, transform, width, height):
    overlay_bounds = transform_bounds(
        overlay.crs, base.crs, *overlay.bounds, densify_pts=21
    )
    left = max(base.bounds.left, overlay_bounds[0])
    bottom = max(base.bounds.bottom, overlay_bounds[1])
    right = min(base.bounds.right, overlay_bounds[2])
    top = min(base.bounds.top, overlay_bounds[3])
    if left >= right or bottom >= top:
        raise ValueError("两幅影像在地理空间上没有重叠区域，无法进行无缝拼接。")

    raw = from_bounds(left, bottom, right, top, transform=transform)
    col0 = max(0, int(math.floor(raw.col_off)))
    row0 = max(0, int(math.floor(raw.row_off)))
    col1 = min(width, int(math.ceil(raw.col_off + raw.width)))
    row1 = min(height, int(math.ceil(raw.row_off + raw.height)))
    if col1 <= col0 or row1 <= row0:
        raise ValueError("重叠区域小于一个输出像元。")
    return Window(col0, row0, col1 - col0, row1 - row0)


def sample_overlap(base_vrt, overlay_vrt, window, band_count):
    max_side = 1600
    scale = min(1.0, max_side / max(window.width, window.height))
    out_h = max(1, int(round(window.height * scale)))
    out_w = max(1, int(round(window.width * scale)))
    shape = (band_count, out_h, out_w)
    indexes = list(range(1, band_count + 1))
    base_data = base_vrt.read(
        indexes=indexes, window=window, out_shape=shape, masked=False
    )
    overlay_data = overlay_vrt.read(
        indexes=indexes, window=window, out_shape=shape, masked=False
    )
    base_mask = base_vrt.dataset_mask(
        window=window, out_shape=(out_h, out_w), resampling=Resampling.nearest
    ) > 0
    overlay_mask = overlay_vrt.dataset_mask(
        window=window, out_shape=(out_h, out_w), resampling=Resampling.nearest
    ) > 0
    common = base_mask & overlay_mask
    valid_count = int(common.sum())
    if valid_count < 64:
        raise ValueError("两幅影像的有效重叠像元过少，无法可靠匀色和消除接缝。")

    # Keep statistics bounded and deterministic for very large overlaps.
    flat_indexes = np.flatnonzero(common)
    if flat_indexes.size > 500_000:
        step = int(math.ceil(flat_indexes.size / 500_000))
        flat_indexes = flat_indexes[::step]
    return (
        base_data.reshape(band_count, -1)[:, flat_indexes].astype(np.float64),
        overlay_data.reshape(band_count, -1)[:, flat_indexes].astype(np.float64),
        valid_count,
    )


def color_bands(dataset: rasterio.DatasetReader) -> list[int]:
    result = []
    for index, interpretation in enumerate(dataset.colorinterp):
        if interpretation != ColorInterp.alpha:
            result.append(index)
    return result or list(range(dataset.count))


def build_color_model(base_samples, overlay_samples, mode, selected_bands):
    model = []
    for band in range(base_samples.shape[0]):
        if mode == "none" or band not in selected_bands:
            model.append(("none",))
            continue

        reference = base_samples[band]
        source = overlay_samples[band]
        finite = np.isfinite(reference) & np.isfinite(source)
        reference = reference[finite]
        source = source[finite]
        if reference.size < 64:
            model.append(("none",))
            continue

        if mode == "linear":
            ref_low, ref_high = np.quantile(reference, (0.02, 0.98))
            src_low, src_high = np.quantile(source, (0.02, 0.98))
            ref_core = reference[(reference >= ref_low) & (reference <= ref_high)]
            src_core = source[(source >= src_low) & (source <= src_high)]
            ref_mean, src_mean = float(ref_core.mean()), float(src_core.mean())
            ref_std, src_std = float(ref_core.std()), float(src_core.std())
            gain = ref_std / src_std if src_std > 1e-12 else 1.0
            gain = float(np.clip(gain, 0.2, 5.0))
            model.append(("linear", gain, ref_mean - gain * src_mean))
        else:
            src_quantiles = np.quantile(source, QUANTILE_LEVELS)
            ref_quantiles = np.quantile(reference, QUANTILE_LEVELS)
            src_unique, unique_indexes = np.unique(src_quantiles, return_index=True)
            ref_unique = ref_quantiles[unique_indexes]
            if src_unique.size < 2:
                model.append(("linear", 1.0, float(np.median(reference) - source[0])))
            else:
                model.append(("histogram", src_unique, ref_unique))
    return model


def apply_color_model(data, model):
    result = data.astype(np.float32, copy=True)
    for band, parameters in enumerate(model):
        if parameters[0] == "linear":
            result[band] = result[band] * parameters[1] + parameters[2]
        elif parameters[0] == "histogram":
            result[band] = np.interp(
                result[band], parameters[1], parameters[2]
            ).astype(np.float32)
    return result


def expanded_window(window: Window, halo: int, width: int, height: int) -> Window:
    col0 = max(0, int(window.col_off) - halo)
    row0 = max(0, int(window.row_off) - halo)
    col1 = min(width, int(window.col_off + window.width) + halo)
    row1 = min(height, int(window.row_off + window.height) + halo)
    return Window(col0, row0, col1 - col0, row1 - row0)


def crop_to_block(array, expanded: Window, block: Window):
    row = int(block.row_off - expanded.row_off)
    col = int(block.col_off - expanded.col_off)
    return array[..., row : row + int(block.height), col : col + int(block.width)]


def cast_output(data, dtype):
    output_dtype = np.dtype(dtype)
    if np.issubdtype(output_dtype, np.integer):
        limits = np.iinfo(output_dtype)
        data = np.rint(np.clip(data, limits.min, limits.max))
    return data.astype(output_dtype, copy=False)


def run(args: argparse.Namespace) -> None:
    if args.feather_distance < 0 or args.feather_distance > MAX_FEATHER_DISTANCE:
        raise ValueError(f"羽化距离必须在 0 到 {MAX_FEATHER_DISTANCE} 像元之间。")
    output_path = os.path.abspath(args.output)
    input_paths = {os.path.abspath(args.base), os.path.abspath(args.overlay)}
    if output_path in input_paths:
        raise ValueError("输出文件不能覆盖输入影像。")
    output_dir = os.path.dirname(output_path) or "."
    os.makedirs(output_dir, exist_ok=True)

    progress(2, "检查输入影像")
    with ExitStack() as stack:
        base = stack.enter_context(rasterio.open(args.base))
        overlay = stack.enter_context(rasterio.open(args.overlay))
        ensure_inputs(base, overlay)
        transform, width, height, _ = grid_for_union(base, overlay)
        estimated_bytes = estimate_output_bytes(width, height, base.count, base.dtypes[0])
        ensure_output_space(output_dir, estimated_bytes)
        overlap_window = intersection_window(
            base, overlay, transform, width, height
        )

        resampling = RESAMPLING[args.resampling]
        vrt_options = dict(
            crs=base.crs,
            transform=transform,
            width=width,
            height=height,
            resampling=resampling,
            # An explicit alpha band is needed because an all-valid source mask
            # otherwise remains all-valid outside the warped source footprint.
            add_alpha=True,
        )
        base_vrt = stack.enter_context(WarpedVRT(base, **vrt_options))
        overlay_vrt = stack.enter_context(WarpedVRT(overlay, **vrt_options))

        progress(10, "分析共同区域")
        base_samples, overlay_samples, valid_overlap = sample_overlap(
            base_vrt, overlay_vrt, overlap_window, base.count
        )
        model = build_color_model(
            base_samples,
            overlay_samples,
            args.color_balance,
            set(color_bands(overlay)),
        )

        profile = base.profile.copy()
        nodata = base.nodata if base.nodata == overlay.nodata else None
        profile.update(
            driver="GTiff",
            width=width,
            height=height,
            count=base.count,
            crs=base.crs,
            transform=transform,
            tiled=True,
            blockxsize=256,
            blockysize=256,
            compress="DEFLATE",
            predictor=2 if np.issubdtype(np.dtype(base.dtypes[0]), np.integer) else 3,
            BIGTIFF="IF_SAFER",
            nodata=nodata,
        )
        temp_fd, temp_path = tempfile.mkstemp(
            prefix=f".{os.path.basename(output_path)}.",
            suffix=".mosaic.tmp.tif",
            dir=output_dir,
        )
        os.close(temp_fd)
        os.unlink(temp_path)
        temp_mask_path = temp_path + ".msk"

        output = None
        try:
            progress(20, "开始匀色与羽化融合")
            with rasterio.Env(GDAL_TIFF_INTERNAL_MASK=True):
                output = stack.enter_context(rasterio.open(temp_path, "w", **profile))
                output.colorinterp = base.colorinterp
                total_blocks = max(
                    1,
                    math.ceil(width / profile["blockxsize"])
                    * math.ceil(height / profile["blockysize"]),
                )
                last_reported_percent = 20

                for block_number, (_, block) in enumerate(output.block_windows(1), 1):
                    expanded = expanded_window(
                        block, args.feather_distance + 2, width, height
                    )
                    base_mask_full = base_vrt.dataset_mask(window=expanded) > 0
                    overlay_mask_full = overlay_vrt.dataset_mask(window=expanded) > 0
                    base_mask = crop_to_block(base_mask_full, expanded, block)
                    overlay_mask = crop_to_block(overlay_mask_full, expanded, block)
                    output_mask = base_mask | overlay_mask

                    indexes = list(range(1, base.count + 1))
                    base_data = base_vrt.read(
                        indexes=indexes, window=block, masked=False
                    ).astype(np.float32)
                    overlay_data = apply_color_model(
                        overlay_vrt.read(indexes=indexes, window=block, masked=False), model
                    )
                    result = np.zeros_like(base_data, dtype=np.float32)
                    only_base = base_mask & ~overlay_mask
                    only_overlay = overlay_mask & ~base_mask
                    common = base_mask & overlay_mask
                    result[:, only_base] = base_data[:, only_base]
                    result[:, only_overlay] = overlay_data[:, only_overlay]

                    if common.any():
                        if args.feather_distance == 0:
                            overlay_weight = np.full(common.shape, 0.5, dtype=np.float32)
                        else:
                            base_distance = np.minimum(
                                distance_transform_edt(base_mask_full), args.feather_distance
                            )
                            overlay_distance = np.minimum(
                                distance_transform_edt(overlay_mask_full), args.feather_distance
                            )
                            denominator = base_distance + overlay_distance
                            overlay_weight_full = np.divide(
                                overlay_distance,
                                denominator,
                                out=np.full_like(denominator, 0.5, dtype=np.float64),
                                where=denominator > 0,
                            )
                            overlay_weight = crop_to_block(
                                overlay_weight_full.astype(np.float32), expanded, block
                            )
                        weight = overlay_weight[common]
                        result[:, common] = (
                            base_data[:, common] * (1.0 - weight)
                            + overlay_data[:, common] * weight
                        )

                    output.write(cast_output(result, base.dtypes[0]), window=block)
                    output.write_mask(output_mask.astype(np.uint8) * 255, window=block)

                    percent = 20 + int(round(76 * block_number / total_blocks))
                    if percent > last_reported_percent:
                        progress(percent, f"处理块 {block_number}/{total_blocks}")
                        last_reported_percent = percent

                output.update_tags(
                    MOSAIC_BASE=os.path.basename(args.base),
                    MOSAIC_OVERLAY=os.path.basename(args.overlay),
                    COLOR_BALANCE=args.color_balance,
                    FEATHER_DISTANCE=str(args.feather_distance),
                    VALID_OVERLAP_PIXELS=str(valid_overlap),
                    MOSAIC_NODATA="preserved" if nodata is not None else "mask-only",
                )
                overview_factors = [
                    factor
                    for factor in (2, 4, 8, 16)
                    if width // factor >= 1 and height // factor >= 1
                ]
                if overview_factors:
                    output.build_overviews(overview_factors, Resampling.average)
                    output.update_tags(ns="rio_overview", resampling="average")

                # Close before replacing on Windows, where an open TIFF handle
                # prevents atomic replacement of the destination.
                output.close()

            # Replace the final file only after the complete TIFF and mask exist.
            old_mask_path = output_path + ".msk"
            os.replace(temp_path, output_path)
            if os.path.exists(temp_mask_path):
                os.replace(temp_mask_path, old_mask_path)
            elif os.path.exists(old_mask_path):
                os.remove(old_mask_path)
        except Exception:
            if output is not None:
                try:
                    output.close()
                except Exception:
                    pass
            for path in (temp_path, temp_mask_path):
                try:
                    if os.path.exists(path):
                        os.remove(path)
                except OSError:
                    pass
            raise
    progress(100, "拼接完成")


def main() -> int:
    try:
        run(parse_args())
        return 0
    except Exception as exc:
        print(f"错误：{exc}", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
