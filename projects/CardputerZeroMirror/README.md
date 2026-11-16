# Cardputer Zero 投屏

Windows x64 配套工具。通过 SSH 显示 Zero 的实体屏幕并转发键盘操作。关闭窗口时会释放仍按住的远程按键。

## 构建和使用

在此目录运行 `pwsh -NoProfile -File .\build.ps1`。构建使用 Windows x64 的 .NET Framework C# 编译器，EXE 和 ZIP 输出到 `dist`。

双击 EXE，填入 Zero 在 IP Panel 显示的地址，点击「连接」，然后点击画面启用按键转发。Windows 需要可用的 OpenSSH 客户端，`pi` 账户需已配置 SSH 公钥登录。首次连接前先核验 Zero 的 SSH 主机密钥，再用 OpenSSH 将其写入 `known_hosts`。Zero 端需要 Python 3，并允许 `pi` 读取屏幕和访问设备自带键盘。

工具栏默认使用「文字增强」，放大时收窄像素之间的过渡并提亮暗色文字。切换到「整数倍」可用同一尺寸查看原始像素；「适应窗口」按原始比例放大到窗口可用尺寸。状态栏显示设备原始分辨率（320×170）、当前绘制尺寸及倍率。

程序记住上次成功连接的地址。需要定制本机发行包时，可在构建前创建不提交到 Git 的 `connection.local.conf`。第一行填初始 IP，第二行可选填本机 `known_hosts` 中已核验的主机别名。
