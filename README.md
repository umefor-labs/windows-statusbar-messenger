# WinStatusBar

Thanh trạng thái mỏng cho Windows, gắn cố định ở cạnh dưới (hoặc trên) màn hình như một taskbar thứ hai. Cửa sổ maximize sẽ chừa chỗ cho nó. Sáng/tối theo Windows.

- Trái: usage của Claude, Codex, Antigravity (thanh trên = 5 giờ, thanh dưới = 7 ngày; di chuột để xem % và giờ reset), rồi tới Messenger.
- Giữa: điều khiển nhạc (bài đang phát trên Windows).
- Phải: NET, CPU, RAM, GPU, nhiệt độ CPU, nhiệt độ GPU.

## Messenger

Gộp từ [Messenger Bubbles](https://github.com/umefor-labs/MessengerBubbles). Thay vì bong bóng nổi, chat hiện thành avatar nhỏ ngay sau khung usage:

- Biểu tượng chat đầu tiên mở danh sách chat, số đỏ là số cuộc chat chưa đọc. Biểu tượng chuyển đỏ khi cần đăng nhập lại hoặc không đọc được danh sách chat (di chuột để xem lý do).
- Mỗi cuộc chat có tin mới thêm một avatar (tối đa 5, còn lại gom vào `+N`). Avatar nhấp nháy, có số tin, kêu một tiếng và hiện thẻ xem trước 5 giây phía trên thanh.
- Bấm avatar mở khung chat ngay phía trên (hoặc dưới, nếu thanh ở trên). Bấm lại, nhấn Esc hoặc bấm ra ngoài để đóng.
- Chuột phải hoặc chuột giữa vào avatar để ẩn nó khỏi thanh. `Ctrl+Alt+M` ẩn/hiện cả cụm.
- Chuột phải vào thanh > `Messenger`: mở cửa sổ đăng nhập Facebook, xem trước, âm báo, tạm im lặng, kích thước khung chat, tắt GPU.

Dùng chung thư mục `%LOCALAPPDATA%\MessengerBubbles` với app gốc nên đã đăng nhập ở app gốc thì không phải đăng nhập lại. Nếu app Messenger Bubbles riêng đang chạy, thanh sẽ không chạy phần Messenger (tránh báo trùng): thoát app riêng rồi mở lại thanh. Rủi ro về điều khoản Facebook giống app gốc, xem README của Messenger Bubbles.

## Nguồn số liệu usage

- Claude: đăng nhập claude.ai trong cửa sổ của app (bấm vào biểu tượng Claude).
- Codex: đọc log cục bộ trong `~/.codex/sessions` (cần dùng Codex ít nhất một lần).
- Antigravity: hỏi tiến trình Antigravity đang chạy trên máy.

## Build

Cần .NET 10 SDK. Test phần Messenger: `dotnet test tests/MessengerBubbles.Tests`.

```
dotnet publish src\WinStatusBar -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

## Cài đặt

Mở PowerShell và chạy:

```powershell
irm https://raw.githubusercontent.com/umefor-labs/windows-statusbar-messenger/main/tools/install.ps1 | iex
```

Windows sẽ hỏi quyền admin một lần. Gỡ cài đặt:

```powershell
irm https://raw.githubusercontent.com/umefor-labs/windows-statusbar-messenger/main/tools/uninstall.ps1 | iex
```

Script cài đặt:

- cài .NET 10 Desktop Runtime nếu máy chưa có,
- cài driver PawnIO qua winget (để đọc nhiệt độ CPU),
- tải `WinStatusBar.exe` bản mới nhất từ Releases và chép vào `%LOCALAPPDATA%\Programs\WinStatusBar`,
- cho thanh bar tự chạy khi đăng nhập (quyền thường, qua khóa Run),
- tạo scheduled task "WinStatusBar Sensors" chạy `WinStatusBar.exe --sensors` quyền admin để đọc nhiệt độ CPU.

Cài từ bản tự build: `.\tools\install.ps1 -Source .\publish\WinStatusBar.exe`. Log cài đặt: `%TEMP%\WinStatusBar-install.log`; log của app: `%LOCALAPPDATA%\WinStatusBar\error.log`.

Phát hành bản mới: đẩy tag dạng `v1.0.0`, GitHub Actions sẽ build và tạo Release kèm exe.

Chuột phải vào thanh để chuyển lên trên/xuống dưới hoặc thoát.

## Giấy phép

MIT, xem [LICENSE](LICENSE).
