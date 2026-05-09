# CleanDesk GitHub 上传清单

这份目录已经按源码仓库整理，适合上传到 GitHub。

## 应提交到仓库的内容

- `CleanDesk.sln`
- `README.md`
- `.gitignore`
- `.gitattributes`
- `.editorconfig`
- `src/`
- `scripts/package.ps1`
- `scripts/generate_icon.py`
- `packaging/`
- `docs/github-upload.md`
- `CleanDesk_logo.png`

## 不应提交的内容

- `dist/`
- `bin/`
- `obj/`
- `*.user`
- `portable-data/`
- 本地日志、转储、临时文件
- 本地开发会话导出

这些内容已在 `.gitignore` 中排除。

## 推荐上传方式

先在 GitHub 创建空仓库，例如：

```text
https://github.com/chrimy666999/CleanDesk
```

再在本机项目根目录执行：

```powershell
git init
git branch -M main
git add .
git commit -m "Initial CleanDesk source release"
git remote add origin https://github.com/chrimy666999/CleanDesk.git
git push -u origin main
```

如果仓库名不是 `CleanDesk`，请替换 remote 地址中的仓库名。

## 发布便携包

源码仓库只提交代码。便携版 ZIP 建议作为 Release 附件上传：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
```

生成文件：

```text
dist\CleanDesk-portable-win-x64.zip
```
