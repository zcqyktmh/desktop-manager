# 桌面管家

Windows 桌面卡片管理工具。将应用、文件、文件夹和书签整理为可调整大小与位置的桌面卡片，并保留 Windows 原生桌面和任务栏。

**Windows 安装包：** 打开 [Releases](../../releases/latest)，下载 `DesktopManager-Setup.exe` 并运行。安装到当前用户目录，不需要管理员权限，也不需要另装 .NET。安装程序可创建开始菜单快捷方式，并可选创建桌面快捷方式。

## 功能

- 桌面图标卡片：显示真实 Windows 图标，可拖入分组，也可拖到桌面文件夹图标中。
- 灵活布局：拖动卡片调整位置，拉动角点调整大小；支持对齐、排序和图标/列表显示。
- 书签卡片：手动添加或导入浏览器书签，按需显示，自动获取网站图标。
- 壁纸：选择本地壁纸或从 Wikimedia Commons 浏览；可定时更换。
- 天气和系统状态小组件：可单独启用并配置展示内容。
- 设置、分组和书签管理支持系统托盘入口；可选择登录 Windows 时启动。

## 使用说明

- 在桌面空白处右键添加网址、新建分组、管理书签或打开设置。
- 桌面图标双击打开；拖动到卡片可改变分组，拖到文件夹图标可把文件实际移入该文件夹。
- 卡片右键菜单可管理卡片层级与显示模式；卡片右上角的操作菜单提供分组操作。
- 退出程序会恢复 Windows 原生桌面显示。

## 数据与隐私

- 布局和设置保存在 `%LOCALAPPDATA%\DesktopManager\data.json`，也可在设置中导出 JSON 备份。
- 桌面项目默认仅按路径显示；只有用户把项目拖到文件夹时才会移动该项目。
- 浏览器书签仅在用户主动导入时读取，不会自动扫描浏览器个人资料。
- 天气数据由 Open-Meteo 提供；壁纸搜索使用 Wikimedia Commons。相关网络功能仅在启用或使用时访问服务。
- 登录启动仅影响当前 Windows 用户。

## 从源码构建

需要 Windows、.NET 10 SDK 和 Inno Setup 6 或更新版本。

```powershell
dotnet publish .\src\DesktopManager\DesktopManager.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\artifacts\publish
iscc .\installer\DesktopManager.iss
```

发布版本：推送 `v*` 格式的 Git 标签后，GitHub Actions 会构建 Windows 安装程序并附加到对应的 GitHub Release。也可以在 Actions 页面手动构建安装包。

## 许可

源码按 [MIT License](LICENSE) 发布。
