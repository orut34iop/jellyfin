# macOS (arm64) Jellyfin dmg 构建说明（含 Jellyfin Web Client）

适用环境：

- Jellyfin Server：`/Volumes/mba2t/projects/jellyfin`（建议分支 `release-10.11.z`）
- Jellyfin Web Client：`/Volumes/mba2t/projects/Jellyfin Web Client`

目标：从本地源码产出可安装的 `Jellyfin.app`，并打包为 `Jellyfin-xxxx.dmg`。

## 1. 环境准备

```bash
xcode-select --install
brew install node create-dmg
```

要求：

- .NET SDK 9.0（与项目一致）
- Node.js 20+（Jellyfin Web Client 依赖）
- macOS 上可执行 `hdiutil`

## 2. 构建 Jellyfin Web Client

```bash
cd "/Volumes/mba2t/projects/Jellyfin Web Client"
npm ci
npm run build:production
```

将构建产物复制到 Server 仓库：

```bash
rm -rf "/Volumes/mba2t/projects/jellyfin/jellyfin-web"
mkdir -p "/Volumes/mba2t/projects/jellyfin/jellyfin-web"
cp -R "/Volumes/mba2t/projects/Jellyfin Web Client/dist/"* "/Volumes/mba2t/projects/jellyfin/jellyfin-web/"
```

## 3. 发布 Server 为 macOS arm64

```bash
cd "/Volumes/mba2t/projects/jellyfin"
git checkout release-10.11.z

# 建议先清理一次，避免旧中间产物影响
rm -rf .build/macos-arm64
dotnet restore Jellyfin.Server/Jellyfin.Server.csproj

OUT_DIR="$PWD/.build/macos-arm64/server-publish"
mkdir -p "$OUT_DIR"

# 默认方式：依赖本机/安装好的 dotnet 运行，保持与 launcher 脚本兼容
# 如果目标机器没有 dotnet runtime，则改为 --self-contained true

dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
  -c Release \
  -f net9.0 \
  -r osx-arm64 \
  --self-contained false \
  /p:PublishSingleFile=false \
  /p:PublishTrimmed=false \
  -o "$OUT_DIR"
```

## 4. 组装 `.app`

推荐直接使用现有官方可运行版做模板，避免 Info.plist 和启动器参数错误。

```bash
WORK_ROOT="/tmp/jellyfin-macos-arm64"
TEMPLATE_APP="/Applications/Jellyfin.app"
APP_DST="$WORK_ROOT/Jellyfin.app"

rm -rf "$WORK_ROOT"
mkdir -p "$WORK_ROOT"
cp -R "$TEMPLATE_APP" "$APP_DST"

rm -rf "$APP_DST/Contents/Resources/jellyfin" \
       "$APP_DST/Contents/Resources/jellyfin-web"

cp -R "/Volumes/mba2t/projects/jellyfin/.build/macos-arm64/server-publish" \
  "$APP_DST/Contents/Resources/jellyfin"
cp -R "/Volumes/mba2t/projects/jellyfin/jellyfin-web" \
  "$APP_DST/Contents/Resources/"

chmod +x "$APP_DST/Contents/MacOS/jellyfin-launcher"
chmod +x "$APP_DST/Contents/Resources/jellyfin/jellyfin"

plutil -replace CFBundleShortVersionString -string "10.11.11" "$APP_DST/Contents/Info.plist"
plutil -replace CFBundleVersion -string "10.11.11" "$APP_DST/Contents/Info.plist"
```

如果你切了 `--self-contained true`，建议先启动一次验证后再走 dmg 打包。

## 5. 打包 dmg

### 5.1 使用 create-dmg（推荐）

```bash
DmgOut="$WORK_ROOT/Jellyfin-arm64-10.11.11.dmg"
rm -f "$DmgOut"

create-dmg \
  --volname "Jellyfin" \
  --window-size 640 360 \
  --icon-size 100 \
  --icon "Jellyfin.app" 160 170 \
  --app-drop-link 460 170 \
  --no-internet-enable \
  "$DmgOut" \
  "$WORK_ROOT"
```

### 5.2 未安装 create-dmg 时

```bash
mkdir -p "$WORK_ROOT/dmg-staging"
cp -R "$APP_DST" "$WORK_ROOT/dmg-staging/"
hdiutil create -srcfolder "$WORK_ROOT/dmg-staging" -volname "Jellyfin" -fs HFS+ -format UDZO -o "$WORK_ROOT/Jellyfin-arm64-10.11.11.dmg"
```

## 6. 安装与验收

```bash
hdiutil attach "$WORK_ROOT/Jellyfin-arm64-10.11.11.dmg"
cp -R "/Volumes/Jellyfin/Jellyfin.app" /Applications/
hdiutil detach "/Volumes/Jellyfin"
open /Applications/Jellyfin.app
```

验收点：

- 能打开 `http://127.0.0.1:8096`
- 能进入首页/媒体库流程
- 目标目录 `~/Library/Application Support/jellyfin` 能正常写入数据
- 日志无致命异常（如启动即崩溃、Web 404/找不到 launcher）

## 7. 常见故障修正

1. Web 页面 404/空白
   - 检查 `Contents/Resources/jellyfin-web` 是否存在 `index.html`、`assets`。
2. 启动失败找不到 `libcoreclr` 或 runtime
   - 用 `--self-contained false` 时，运行机需有 .NET runtime。
   - 无 runtime 时改 `--self-contained true` 重新发布。
3. 文件权限异常
   - 确保 `jellyfin-launcher` 与 `jellyfin` 为可执行文件。

## 8. 提交

```bash
cd "/Volumes/mba2t/projects/jellyfin"
git add docs/macos-arm64-dmg-build-guide-zh.md
git commit -m "docs: add macos arm64 dmg build guide"
git push
```
