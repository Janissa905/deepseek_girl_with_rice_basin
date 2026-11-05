以「蓝色大肥鱼」立绘和用户提供的动画视频为素材制作的 Windows 桌面宠物，会在 DeepSeek Harness 启动后自动出现，站在任务栏上方（默认屏幕右下角）。

## 安装！！！！！

把压缩包解压到任意可写目录，然后双击文件夹中的：`install.bat`


卸载（保留程序文件）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1
```

## 余额从哪里来

读取本机 DSH 已登录的账户凭据，不保存密码，也不需要额外 API Key：

- 凭据：`%USERPROFILE%\.dsh\.credentials.yaml` 中的 `deepseek-account-platform/default`
- 请求：`GET https://platform.deepseek.com/api/v0/users/get_user_summary`
- 请求头：`x-dsh-auth-token` + DSH 客户端标识
- 只读 `normal_wallets`，保留两位小数
- **每次左键点击都会重新查询**，不需要再单独点“刷新余额”

凭据只在本机内存中使用，不写入日志。未登录/过期时气泡显示「读取失败 / 请重新登录 DSH」。

## 文件说明

```
Little_DS_Girl\
  DSHPet.exe             主程序（WPF，.NET Framework 4.8）
  frames_sheet_n.png     75 帧视频动画（8×10，每帧 240×292，含末帧过渡）
  clang.wav              铁盆扣头音效
  pet.ico                托盘图标
  watch.ps1              守护：DSH 启动→开桌宠，退出→关桌宠
  watch.vbs              隐藏启动 watch.ps1
  install.ps1            安装开机启动
  uninstall.ps1          移除开机启动
  Pet.cs                 源码
  build.ps1              编译脚本
  position.json          拖动位置（运行时生成）
  stopped.flag           手动退出标记（运行时生成）
  pet.log                启动/错误日志（运行时生成）
```

## 重新编译

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

编译前先退出桌宠，否则 exe 被占用会失败。

## 调整参数

- 默认位置：`Pet.cs` 里的 `DefaultLeft = 1333.6; DefaultTop = 597.6;`（即当前屏幕上的位置）。
- 尺寸：`PetWindow` 构造函数里的 `Width = 210; Height = 245;` 与 `BuildUi()` 里的 `190×231`。
- 动画速度：`UpdateReaction()` 里的 `t × 15.0`，以及结束时间 `t >= 4.95`。
- 气泡停留：`UpdateBubble()` 里的 `age > 2.0`。
- 余额缓存：`BalanceService.Stale` 的 25 秒。
- 换视频：改 `video_to_frames.py` 的路径和时间步长，重新生成 `frames_sheet_v.png`。


## 排查

- **不出现**：确认 DSH 在运行；任务管理器里应有 `DSHPet.exe` 和命令行含 `watch.ps1` 的 `powershell.exe`。
- **没有余额**：先在 DSH 确认已登录；看 `pet.log` 的 `balance error`。
- **被遮挡**：程序每 400ms 将自己提升到置顶窗口层；全屏独占程序仍可能遮挡。
