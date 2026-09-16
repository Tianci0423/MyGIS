import argparse
import os
import shutil
import uuid

import numpy as np
import rasterio
from affine import Affine
from rasterio.enums import MaskFlags, Resampling
from rasterio.vrt import WarpedVRT
from rasterio.windows import Window


RESAMPLING = {
    "nearest": Resampling.nearest,
    "bilinear": Resampling.bilinear,
    "cubic": Resampling.cubic,
    "lanczos": Resampling.lanczos,
}
WINDOW_SIZE = 1024


def rasterio_transform(values):
    gt = [float(value) for value in values]
    return Affine(gt[1], gt[2], gt[0], gt[4], gt[5], gt[3])


def iter_windows(width, height):
    for row in range(0, height, WINDOW_SIZE):
        h = min(WINDOW_SIZE, height - row)
        for col in range(0, width, WINDOW_SIZE):
            w = min(WINDOW_SIZE, width - col)
            yield Window(col, row, w, h)


def report(copied, total):
    percent = 100 if total <= 0 else min(100, int(copied * 100 / total))
    print(f"Register: {percent}%", flush=True)


def copy_file(source, target):
    total = os.path.getsize(source)
    copied = 0
    last = -1
    with open(source, "rb") as src, open(target, "xb") as dst:
        while True:
            block = src.read(8 * 1024 * 1024)
            if not block:
                break
            dst.write(block)
            copied += len(block)
            percent = 90 if total <= 0 else min(90, int(copied * 90 / total))
            if percent != last:
                print(f"Register: {percent}%", flush=True)
                last = percent


def copy_sidecar(source, output, suffix):
    source_sidecar = source + suffix
    output_sidecar = output + suffix
    if os.path.exists(source_sidecar):
        shutil.copy2(source_sidecar, output_sidecar)
    elif os.path.exists(output_sidecar):
        os.remove(output_sidecar)


def georeference_only(moving, reference, output, transform):
    output_dir = os.path.dirname(os.path.abspath(output))
    os.makedirs(output_dir, exist_ok=True)
    temp = os.path.join(output_dir, f".{os.path.basename(output)}.{uuid.uuid4().hex}.tmp.tif")
    try:
        copy_file(moving, temp)
        with rasterio.open(reference) as ref:
            if ref.crs is None:
                raise ValueError("基准影像没有坐标系，无法写入配准结果。")
            reference_crs = ref.crs
        with rasterio.open(temp, "r+") as dst:
            dst.transform = transform
            dst.crs = reference_crs
        if os.path.exists(output):
            os.remove(output)
        os.replace(temp, output)
        copy_sidecar(moving, output, ".msk")
        copy_sidecar(moving, output, ".ovr")
        report(100, 100)
    except Exception:
        if os.path.exists(temp):
            os.remove(temp)
        raise


def copy_metadata(src, dst):
    tags = src.tags()
    if tags:
        dst.update_tags(**tags)
    for band in range(1, src.count + 1):
        band_tags = src.tags(band)
        if band_tags:
            dst.update_tags(band, **band_tags)
    for attribute in ("descriptions", "units", "scales", "offsets", "colorinterp"):
        try:
            setattr(dst, attribute, getattr(src, attribute))
        except Exception:
            pass


def resample_to_reference(moving, reference, output, corrected_transform, resampling):
    output_dir = os.path.dirname(os.path.abspath(output))
    os.makedirs(output_dir, exist_ok=True)
    temp = os.path.join(output_dir, f".{os.path.basename(output)}.{uuid.uuid4().hex}.tmp.tif")
    try:
        with rasterio.open(moving) as src, rasterio.open(reference) as ref:
            if ref.crs is None:
                raise ValueError("基准影像没有坐标系，无法建立目标网格。")
            profile = src.profile.copy()
            profile.update(
                driver="GTiff",
                crs=ref.crs,
                transform=ref.transform,
                width=ref.width,
                height=ref.height,
                tiled=True,
                blockxsize=512,
                blockysize=512,
                compress="deflate",
                predictor=3 if np.issubdtype(np.dtype(src.dtypes[0]), np.floating) else 2,
                BIGTIFF="IF_SAFER",
            )
            vrt_options = dict(
                src_crs=ref.crs,
                src_transform=corrected_transform,
                crs=ref.crs,
                transform=ref.transform,
                width=ref.width,
                height=ref.height,
                resampling=resampling,
                warp_mem_limit=512,
            )
            if src.nodata is not None:
                vrt_options["src_nodata"] = src.nodata
                vrt_options["nodata"] = src.nodata
                profile["nodata"] = src.nodata

            needs_mask = any(
                MaskFlags.all_valid not in flags
                and MaskFlags.alpha not in flags
                and MaskFlags.nodata not in flags
                for flags in src.mask_flag_enums
            )
            total = ref.width * ref.height
            completed = 0
            last = -1
            with WarpedVRT(src, **vrt_options) as vrt, rasterio.open(temp, "w", **profile) as dst:
                copy_metadata(src, dst)
                for window in iter_windows(ref.width, ref.height):
                    dst.write(vrt.read(window=window), window=window)
                    if needs_mask:
                        dst.write_mask(vrt.dataset_mask(window=window), window=window)
                    completed += int(window.width * window.height)
                    percent = min(100, int(completed * 100 / total))
                    if percent != last:
                        print(f"Register: {percent}%", flush=True)
                        last = percent

        if os.path.exists(output):
            os.remove(output)
        if os.path.exists(output + ".msk"):
            os.remove(output + ".msk")
        os.replace(temp, output)
        if os.path.exists(temp + ".msk"):
            os.replace(temp + ".msk", output + ".msk")
        report(100, 100)
    except Exception:
        for path in (temp, temp + ".msk"):
            if os.path.exists(path):
                os.remove(path)
        raise


def main():
    parser = argparse.ArgumentParser(description="GeoVision multi-temporal registration")
    parser.add_argument("--moving", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--mode", choices=("georef", "resample"), required=True)
    parser.add_argument("--resampling", choices=tuple(RESAMPLING), default="bilinear")
    parser.add_argument("--geotransform", nargs=6, required=True, type=float)
    args = parser.parse_args()

    moving = os.path.abspath(args.moving)
    reference = os.path.abspath(args.reference)
    output = os.path.abspath(args.output)
    if output.lower() in {moving.lower(), reference.lower()}:
        raise ValueError("输出文件不能覆盖输入影像。")
    transform = rasterio_transform(args.geotransform)
    if args.mode == "georef":
        georeference_only(moving, reference, output, transform)
    else:
        resample_to_reference(moving, reference, output, transform, RESAMPLING[args.resampling])


if __name__ == "__main__":
    main()
