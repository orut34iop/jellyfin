# macOS (arm64) Jellyfin dmg 构建说明（含 Jellyfin Web Client）

适用环境：

- Jellyfin Server：本机的 jellyfin 源码仓库（建议分支 `release-10.11.z`）
- Jellyfin Web Client：本机的 jellyfin-web 源码仓库

目标：从本地源码产出可安装的 `Jellyfin.app`，并打包为 `Jellyfin-xxxx.dmg`。

## 0. 关于官方 macOS .app 的真实结构（先读）

10.11.11 官方 macOS dmg 中的 `Jellyfin.app` 与传统印象不同 —— **没有 `jellyfin-launcher` 脚本，也没有 `Contents/Resources/jellyfin/` 子目录**。真实布局：

- `Contents/MacOS/Jellyfin Server` — Swift 原生菜单栏启动器（universal x86_64+arm64，`Info.plist` 中 `CFBundleExecutable` = `Jellyfin Server`，`LSUIElement=true` 所以没有 Dock 图标）。
- `Contents/MacOS/jellyfin` — dotnet apphost（arm64），由原生启动器拉起。
- `Contents/MacOS/*.dll`、`Contents/MacOS/libcoreclr.dylib`、`libhostfxr.dylib`、`libhostpolicy.dylib` 等 — **整个 dotnet self-contained publish 平铺在 MacOS/ 下**。
- `Contents/MacOS/ffmpeg`、`Contents/MacOS/ffprobe` — 内置的媒体工具。
- `Contents/MacOS/ServerSetupApp/`、`Contents/MacOS/Resources/Configuration/` — 启动器附带资源。
- `Contents/Resources/jellyfin-web/` — Web 客户端静态文件。
- `Contents/Resources/AppIcon.icns`、storyboard、`LaunchAtLogin_LaunchAtLogin.bundle` — 原生启动器使用。

原生启动器运行时会自动以下列参数启动 dotnet 服务：

```
<bundle>/Contents/MacOS/jellyfin \
  --webdir   <bundle>/Contents/Resources/jellyfin-web \
  --ffmpeg   <bundle>/Contents/MacOS/ffmpeg \
  --datadir  ~/Library/Application Support/jellyfin
```

由此推出两条**硬性约束**（与本文档早期版本不同）：

1. **必须 `--self-contained true` publish** —— 否则 `MacOS/` 内缺少 `libcoreclr.dylib` 等运行时；GUI 启动不继承 shell `PATH`，会找不到 .NET runtime。
2. **替换 Mach-O 文件后必须重新 codesign**（ad-hoc 即可） —— 否则会破坏官方签名导致 macOS 拒绝运行。

## 1. 环境准备

```bash
xcode-select --install
brew install node create-dmg
```

要求：

- .NET SDK 9.0（与项目一致）
- Node.js 20+（Jellyfin Web Client 依赖；本机若有 Node 22/26 一般也兼容）
- macOS 上可执行 `hdiutil`、`codesign`

## 2. 设置变量

请按你的实际路径调整以下变量；下文所有命令都引用它们。

```bash
SERVER_DIR="/Users/wiz/dev/jellyfin"
WEB_DIR="/Users/wiz/dev/Jellyfin Web Client"   # 路径含空格也支持，注意加引号
WORK_ROOT="/tmp/jellyfin-macos-arm64"
VERSION_TAG="10.11.11-local"
```

## 3. 构建 Jellyfin Web Client

```bash
cd "$WEB_DIR"
npm ci
rm -rf dist
npm run build:production
```

将构建产物复制到 Server 仓库：

```bash
rm -rf "$SERVER_DIR/jellyfin-web"
mkdir -p "$SERVER_DIR/jellyfin-web"
cp -R "$WEB_DIR/dist/"* "$SERVER_DIR/jellyfin-web/"
```

验证：`$SERVER_DIR/jellyfin-web/index.html` 应存在。

## 4. Publish Server 为 macOS arm64（self-contained）

```bash
cd "$SERVER_DIR"
git checkout release-10.11.z

# 清理旧产物，避免污染
rm -rf .build/macos-arm64
OUT_DIR="$SERVER_DIR/.build/macos-arm64/server-publish"
mkdir -p "$OUT_DIR"

dotnet restore Jellyfin.Server/Jellyfin.Server.csproj

dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
  -c Release \
  -f net9.0 \
  -r osx-arm64 \
  --self-contained true \
  /p:PublishSingleFile=false \
  /p:PublishTrimmed=false \
  -o "$OUT_DIR"
```

完成后 `$OUT_DIR` 应包含 `jellyfin`（arm64 apphost）、`libcoreclr.dylib`、`libhostfxr.dylib`、`libhostpolicy.dylib` 以及所有托管 DLL（约 480 个文件）。如果少了 `libcoreclr.dylib`，说明没加 `--self-contained true`。

## 5. 组装 `.app`

复用官方 `.app` 作为模板，**保留原生启动器与原生工具**，覆盖 dotnet 部分与 web 客户端。

```bash
TEMPLATE_APP="/Applications/Jellyfin.app"   # 没有的话先从官网下载 dmg 装一次
APP_DST="$WORK_ROOT/Jellyfin.app"

rm -rf "$WORK_ROOT"
mkdir -p "$WORK_ROOT"
cp -R "$TEMPLATE_APP" "$APP_DST"

# 把官方 .app 中必须保留的原生件先挪走
PRESERVE_DIR="/tmp/jf-preserve"
rm -rf "$PRESERVE_DIR"
mkdir -p "$PRESERVE_DIR"
for item in "Jellyfin Server" ffmpeg ffprobe ServerSetupApp Resources; do
  if [ -e "$APP_DST/Contents/MacOS/$item" ]; then
    mv "$APP_DST/Contents/MacOS/$item" "$PRESERVE_DIR/"
  fi
done

# 清空 MacOS/ 中其余项（全部都是要被替换的 dotnet 产物）
find "$APP_DST/Contents/MacOS" -mindepth 1 -maxdepth 1 -exec rm -rf {} +

# 把原生件放回去
mv "$PRESERVE_DIR/"* "$APP_DST/Contents/MacOS/"

# 把我们的 self-contained publish 平铺进 MacOS/
cp -R "$OUT_DIR/." "$APP_DST/Contents/MacOS/"

# 可选：移除 pdb 减小体积
find "$APP_DST/Contents/MacOS" -maxdepth 1 -name "*.pdb" -delete

# 替换 web 客户端
rm -rf "$APP_DST/Contents/Resources/jellyfin-web"
cp -R "$SERVER_DIR/jellyfin-web" "$APP_DST/Contents/Resources/jellyfin-web"

# 设置可执行权限
chmod +x "$APP_DST/Contents/MacOS/Jellyfin Server"
chmod +x "$APP_DST/Contents/MacOS/jellyfin"
chmod +x "$APP_DST/Contents/MacOS/ffmpeg" "$APP_DST/Contents/MacOS/ffprobe" "$APP_DST/Contents/MacOS/createdump" 2>/dev/null

# 更新版本号
plutil -replace CFBundleShortVersionString -string "$VERSION_TAG" "$APP_DST/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$VERSION_TAG" "$APP_DST/Contents/Info.plist"

# 重新签名（必须）：替换 Mach-O 会让官方签名失效
rm -rf "$APP_DST/Contents/_CodeSignature"
codesign --force --deep --sign - "$APP_DST"
```

打 dmg 前**强烈建议先就地试启动一次**：

```bash
open -n "$APP_DST"
sleep 12
curl -sS -o /dev/null -w "HTTP %{http_code}\n" http://127.0.0.1:8096/web/index.html  # 期望 200
curl -sS http://127.0.0.1:8096/System/Info/Public  # 期望返回 Version=10.11.11
pkill -f "$WORK_ROOT/Jellyfin.app/Contents/MacOS/"
```

## 6. 打包 dmg

### 6.1 使用 create-dmg（推荐）

`create-dmg` 把指定源目录里所有内容塞进 dmg。**用单独的 staging 目录**只放 `.app`，避免把 `dmg-src` 等无关项也打进去。

```bash
DmgOut="$WORK_ROOT/Jellyfin-arm64-$VERSION_TAG.dmg"
rm -f "$DmgOut"

DMG_STAGE="$WORK_ROOT/dmg-src"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -R "$APP_DST" "$DMG_STAGE/"

create-dmg \
  --volname "Jellyfin" \
  --window-size 640 360 \
  --icon-size 100 \
  --icon "Jellyfin.app" 160 170 \
  --app-drop-link 460 170 \
  --no-internet-enable \
  "$DmgOut" \
  "$DMG_STAGE"
```

### 6.2 未安装 create-dmg 时

```bash
mkdir -p "$WORK_ROOT/dmg-staging"
cp -R "$APP_DST" "$WORK_ROOT/dmg-staging/"
hdiutil create \
  -srcfolder "$WORK_ROOT/dmg-staging" \
  -volname "Jellyfin" \
  -fs HFS+ -format UDZO \
  -o "$WORK_ROOT/Jellyfin-arm64-$VERSION_TAG.dmg"
```

## 7. 安装与验收

```bash
DmgOut="$WORK_ROOT/Jellyfin-arm64-$VERSION_TAG.dmg"
hdiutil attach "$DmgOut" -nobrowse -quiet
MNT=$(ls -d /Volumes/Jellyfin* | head -1)

# 先停掉旧实例再覆盖，避免文件被占用
pkill -f "/Applications/Jellyfin.app/Contents/MacOS/" 2>/dev/null || true
sleep 1
rm -rf /Applications/Jellyfin.app
cp -R "$MNT/Jellyfin.app" /Applications/
hdiutil detach "$MNT" -quiet

open /Applications/Jellyfin.app
sleep 15
curl -sS -o /dev/null -w "HTTP %{http_code}\n" http://127.0.0.1:8096/web/index.html
curl -sS http://127.0.0.1:8096/System/Info/Public; echo
```

验收点：

- `http://127.0.0.1:8096/web/index.html` 返回 HTTP 200，body 包含 `<title>Jellyfin</title>`
- `/System/Info/Public` 返回正确 `Version`
- `~/Library/Application Support/jellyfin/{config,data,cache,log}` 已创建
- 进程树：`Jellyfin Server`（菜单栏 launcher） → `jellyfin --webdir … --ffmpeg … --datadir …`
- 最新日志末尾出现 `Core startup complete` / `Startup complete`，无 `[ERR]/[FTL]/Unhandled`

## 8. 常见故障

1. **Finder 双击 .app 立即闪退 / 日志为空**
   - 多半是没重新 codesign，或 publish 不是 self-contained 导致缺 `libcoreclr.dylib`。
   - 用 `codesign -dv /Applications/Jellyfin.app` 看签名是否还在（被替换文件后旧签名会失效）。
   - 用 `ls /Applications/Jellyfin.app/Contents/MacOS/libcoreclr.dylib` 检查运行时。
2. **Web 页面 404 / 空白**
   - 检查 `Contents/Resources/jellyfin-web/index.html`、`assets/` 是否齐全。
3. **端口 8096 被占**
   - `lsof -nP -iTCP:8096`；旧 Jellyfin 进程没退干净时常见。
4. **macOS Gatekeeper 拦截**
   - 第一次启动若被拦，可在「系统设置 → 隐私与安全性」放行；或对开发自用执行：`xattr -dr com.apple.quarantine /Applications/Jellyfin.app`。

## 9. 提交

```bash
cd "$SERVER_DIR"
git add docs/macos-arm64-dmg-build-guide-zh.md
git commit -m "docs: update macos arm64 dmg build guide"
git push
```
