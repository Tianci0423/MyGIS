# GeoVision

GeoVision 是一个基于 .NET 8 WPF 的桌面 GIS 应用，用于栅格影像浏览、图层管理、属性查看、波段显示、波段计算、影像配准和影像融合等实验性功能。

项目主体使用 C# 编写，界面层基于 WPF，栅格数据处理依赖 GDAL、Mapsui、SkiaSharp 和 OpenTK。影像配准与融合相关的重计算流程通过本地 Python 运行环境调用脚本完成。

## 仓库内容说明

本仓库主要保存项目源码、工程文件和 Git 配置，不包含以下本地运行/生成内容：

- `python_env/`：本地 Python 运行环境
- `fusion_model/`：融合模型代码目录
- `*.pth`、`*.pt`、`*.onnx`：模型权重文件
- `bin/`、`obj/`：编译输出目录
- `.vs/`、`*.user`：本机 IDE 与用户配置

因此，克隆仓库后如果需要运行影像配准或融合功能，需要在本地准备对应的 Python 运行环境、脚本和模型权重。

## .NET 项目依赖

项目目标框架为 `net8.0-windows10.0.19041`，主要 NuGet 包包括：

| Package | Version |
| --- | --- |
| Mapsui.Extensions | 5.0.2 |
| Mapsui.Wpf | 5.0.2 |
| MaxRev.Gdal.Core | 3.13.1.534 |
| MaxRev.Gdal.WindowsRuntime.Minimal | 3.13.1.534 |
| OpenTK | 4.9.4 |
| OpenTK.GLWpfControl | 4.3.6 |
| SkiaSharp.Views.WPF | 3.119.1 |

## Python 环境依赖

以下版本来自当前仓库内 `python_env\runtime\python` 的 `pip freeze` 输出：

```text
affine==2.4.0
annotated-doc==0.0.4
anyio==4.13.0
attrs==26.1.0
certifi==2026.5.20
click==8.4.1
click-plugins==1.1.1.2
cligj==0.7.2
colorama==0.4.6
contourpy==1.3.3
cycler==0.12.1
einops==0.8.2
filelock==3.29.0
fonttools==4.63.0
fsspec==2026.4.0
h11==0.16.0
h5py==3.16.0
hf-xet==1.5.0
httpcore==1.0.9
httpx==0.28.1
huggingface_hub==1.16.4
idna==3.16
Jinja2==3.1.6
kiwisolver==1.5.0
markdown-it-py==4.2.0
MarkupSafe==3.0.3
matplotlib==3.10.9
mdurl==0.1.2
mpmath==1.3.0
networkx==3.6.1
numpy==2.4.6
packaging==26.2
pillow==12.2.0
Pygments==2.20.0
pyparsing==3.3.2
python-dateutil==2.9.0.post0
PyYAML==6.0.3
rasterio==1.4.4
rich==15.0.0
safetensors==0.7.0
scipy==1.17.1
shellingham==1.5.4
six==1.17.0
sympy==1.14.0
timm==1.0.27
torch==2.11.0+cu128
torchvision==0.26.0+cu128
tqdm==4.67.3
typer==0.25.1
typing_extensions==4.15.0
```
