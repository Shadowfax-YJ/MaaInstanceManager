# MAA 实例管理器

MAA 实例管理器用于批量创建和维护多个 MAA 本体实例。它独立于 MAA 本体仓库发布和管理版本，适合需要多开、分组、端口映射、批量启动或备份实例的使用场景。

## 功能

- 从本地 MAA Release 压缩包创建多个实例。
- 从指定 Git 仓库读取本体版本标签，并从 GitHub Release 自动下载 Windows x64 压缩包到缓存目录。
- 复制已配置好的实例，并自动分配 ADB 端口。
- 按分组管理实例，支持分组筛选和批量设置分组。
- 批量重映射 ADB 端口。
- 启动、关闭、刷新和打开实例目录。
- 将选中的实例打包为 zip 备份。
- 删除选中的实例及其磁盘目录。

## 使用方式

自动获取版本需要本机 `git` 可用。自动下载当前支持 GitHub Release 中的 zip 资产，默认优先匹配 `MAA-<tag>-win-x64.zip`；如果 GitHub API 被限流，可以在环境变量中设置 `GITHUB_TOKEN` 或 `GH_TOKEN`。

1. 在“本体 Git 仓库”填写 MAA 本体仓库地址，例如 `https://github.com/MaaAssistantArknights/MaaAssistantArknights.git`。
2. 点击“获取版本”，在“本体版本”中选择需要的标签。
3. 点击“下载缓存”，工具会把对应 GitHub Release 的 Windows x64 zip 下载到缓存目录，并自动填入“Release 压缩包”。
4. 选择实例总目录，设置名称前缀、数量、目标分组和 ADB 端口范围。
5. 点击“从 Release 创建”或“复制选中实例”创建实例。

如果自动下载不适用于目标仓库，也可以手动选择本地 Release zip 后创建实例。

## 本地构建

需要 Windows 和 .NET 10 SDK。

```powershell
dotnet restore .\MaaInstanceManager.csproj -p:Platform=x64
dotnet publish .\MaaInstanceManager.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish
```

## 发布

本仓库使用独立版本号。创建 `v*` 标签后，GitHub Actions 会构建 Windows x64 自包含版本，并把 `MAAInstanceManager-<tag>-win-x64.zip` 上传到对应 Release。

## 仓库描述

MAA 多实例批量创建、分组、打包、启动和版本缓存管理工具。
