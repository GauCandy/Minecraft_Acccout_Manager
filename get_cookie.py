import json
import os
import subprocess
import sys
import time

def save_cookies(cookies, netscape_file="cookies.txt", json_file="cookies.json"):
    # 1. Lưu file JSON
    with open(json_file, "w", encoding="utf-8") as f:
        json.dump(cookies, f, indent=2, ensure_ascii=False)

    # 2. Lưu file Netscape .txt
    lines = [
        "# Netscape HTTP Cookie File",
        "# Created by Minecraft Cookie Helper",
        ""
    ]
    now = int(time.time())
    for c in cookies:
        domain = c.get("domain", "")
        flag = "TRUE" if domain.startswith(".") else "FALSE"
        path = c.get("path", "/")
        secure = "TRUE" if c.get("secure", False) else "FALSE"
        expiry = int(c.get("expiry", now + 31536000))
        name = c.get("name", "")
        value = c.get("value", "")
        if name and value:
            lines.append(f"{domain}\t{flag}\t{path}\t{secure}\t{expiry}\t{name}\t{value}")

    netscape_content = "\n".join(lines) + "\n"
    with open(netscape_file, "w", encoding="utf-8") as f:
        f.write(netscape_content)

    # 3. Tự động chép vào Clipboard Windows
    try:
        cmd = ["powershell", "-NoProfile", "-Command", "Set-Clipboard -Value ([System.IO.File]::ReadAllText('cookies.txt'))"]
        subprocess.run(cmd, check=False)
        clipboard_copied = True
    except Exception:
        clipboard_copied = False

    return len(cookies), clipboard_copied

def main():
    print("=" * 60)
    print("  🍪 CÔNG CỤ TỰ ĐỘNG ĐĂNG NHẬP & TRÍCH XUẤT COOKIE MINECRAFT")
    print("=" * 60)
    print("\nĐang khởi động trình duyệt Chrome...")

    driver = None
    try:
        import undetected_chromedriver as uc
        options = uc.ChromeOptions()
        options.add_argument("--start-maximized")
        driver = uc.Chrome(options=options)
    except Exception as e:
        print(f"Lưu ý: Không dùng được undetected_chromedriver ({e}), chuyển sang selenium chuẩn...")
        try:
            from selenium import webdriver
            from selenium.webdriver.chrome.service import Service
            options = webdriver.ChromeOptions()
            options.add_argument("--start-maximized")
            driver = webdriver.Chrome(options=options)
        except Exception as e2:
            print(f"\n[LỖI] Không thể mở Chrome: {e2}")
            print("Vui lòng đảm bảo bạn đã cài đặt Google Chrome trên máy tính.")
            input("\nNhấn Enter để thoát...")
            return

    login_url = (
        "https://sisu.xboxlive.com/connect/XboxLive/?"
        "state=login&cobrandId=8058f65d-ce06-4c30-9559-473c9275a65d&"
        "tid=896928775&ru=https%3A%2F%2Fwww.minecraft.net%2Fen-us%2Flogin&"
        "aid=1142970254&as=1"
    )

    print("\n[+] Đang mở trang đăng nhập Microsoft / Minecraft...")
    driver.get(login_url)

    print("\n" + "-" * 60)
    print("👉 HƯỚNG DẪN:")
    print("  1. Hãy nhập Email, Mật khẩu và mã 2FA (nếu có) trên cửa sổ Chrome vừa hiện.")
    print("  2. Khi đăng nhập thành công và trang chuyển sang Minecraft / Tài khoản:")
    print("     Quay lại cửa sổ này và NHẤN PHÍM ENTER.")
    print("-" * 60)

    try:
        print("\n⏳ Đang đợi bạn đăng nhập... (Nhấn Enter ở đây ngay sau khi đăng nhập xong)")
        while True:
            try:
                current_url = driver.current_url
                if "minecraft.net" in current_url and "#" in current_url:
                    print("\n[✓] Phát hiện đã đăng nhập thành công vào Minecraft!")
                    break
            except Exception:
                break

            import msvcrt
            if msvcrt.kbhit():
                key = msvcrt.getch()
                if key in (b'\r', b'\n'):
                    break
            time.sleep(1)

        print("\n[+] Đang trích xuất toàn bộ cookie đăng nhập...")
        cookies = driver.get_cookies()

        if not cookies:
            print("[!] Không tìm thấy cookie nào trong phiên làm việc.")
            return

        total, clip_ok = save_cookies(cookies)

        print("\n" + "=" * 60)
        print(f"🎉 THÀNH CÔNG! ĐÃ LẤY ĐƯỢC {total} COOKIES!")
        print("=" * 60)
        print("📁 Đã xuất ra 2 file:")
        print(f"   1. {os.path.abspath('cookies.txt')}  (Định dạng Netscape)")
        print(f"   2. {os.path.abspath('cookies.json')} (Định dạng JSON)")
        if clip_ok:
            print("📋 Cookie đã được TỰ ĐỘNG CHÉP VÀO CLIPBOARD (bộ nhớ tạm)!")
        print("\n👉 BÂY GIỜ BẠN CHỈ CẦN:")
        print("   * Mở Minecraft Cookie Manager.")
        print("   * Nhấn nút 'Dán Clipboard' hoặc kéo thả file cookies.txt / cookies.json vào app!")
        print("=" * 60)

    except KeyboardInterrupt:
        print("\nĐã hủy bởi người dùng.")
    except Exception as ex:
        print(f"\n[LỖI] Có sự cố xảy ra: {ex}")
    finally:
        if driver:
            try:
                driver.quit()
            except Exception:
                pass

    input("\nNhấn phím Enter để đóng...")

if __name__ == "__main__":
    main()
