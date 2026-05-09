# CleanDesk

CleanDesk 是一款面向 Windows 11 的便携式桌面图标整理工具。它会扫描真实桌面图标，记录原始布局，并把桌面图标收纳进可拖拽、可调整大小的半透明桌面盒子中；用户可以随时一键恢复到使用 CleanDesk 前的桌面状态。

## 功能概览

- 自动扫描 Windows 桌面图标并创建默认收纳盒
- 尽量保留原生 Shell 图标、快捷方式图标和文件类型图标
- 盒子支持拖拽、调整大小、折叠/展开和透明度调节
- 支持靠近屏幕边缘、网格线和其他盒子边缘时吸附对齐
- 盒子内图标支持双击打开、右键菜单、属性、复制路径、删除到回收站等常见操作
- 托盘菜单支持显示/隐藏盒子、自动整理、恢复桌面、设置中心、开机自启动和退出
- 异常退出后提供继续使用布局、恢复原始桌面布局和安全模式选项

CleanDesk 不会在整理、解散或恢复时删除用户文件。

## 项目结构

```text
CleanDesk.sln
src/
  CleanDesk.App/          # Windows 桌面应用源码
scripts/
  package.ps1             # 生成便携版发布包
  generate_icon.py         # 从 PNG 生成 Windows .ico
packaging/
  README-package.txt       # 便携包内容说明
CleanDesk_logo.png         # 项目图标源文件
```

## 开发环境

- Windows 11
- .NET 9 SDK
- Python 3 + Pillow（仅在重新生成图标时需要）

最终用户使用 `scripts\package.ps1` 生成的便携包时，不需要安装 .NET SDK、Python 或其他开发依赖。

## 构建

```powershell
dotnet build CleanDesk.sln -c Release
```

## 生成便携包

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
```

默认输出：

- `dist\CleanDesk-portable\CleanDesk.exe`
- `dist\CleanDesk-portable-win-x64.zip`

`dist` 是构建产物目录，已经通过 `.gitignore` 排除。建议把生成的 ZIP 上传到 GitHub Releases，而不是提交到源码仓库。

## 运行便携版

1. 解压 `CleanDesk-portable-win-x64.zip`
2. 双击 `CleanDesk.exe`
3. 首次启动后，CleanDesk 会扫描桌面图标并创建默认收纳盒

便携版会在程序目录下创建 `portable-data`，用于保存配置、备份、日志和缓存。

## 上传到 GitHub

如果仓库尚未创建，先在 GitHub 上创建一个空仓库，例如：

```text
https://github.com/chrimy666999/CleanDesk
```

然后在项目根目录执行：

```powershell
git init
git branch -M main
git add .
git commit -m "Initial CleanDesk source release"
git remote add origin https://github.com/chrimy666999/CleanDesk.git
git push -u origin main
```

如果你使用的是其他仓库名，把上面的 `CleanDesk.git` 替换成实际仓库名。
