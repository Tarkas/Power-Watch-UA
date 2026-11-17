
English Version

PowerWatchUA is a Windows utility designed to automatically power off your laptop or PC based on scheduled power outages in Ukraine. It monitors specific Telegram channels for schedule updates, countdowns to blackouts, and safely shuts down the system.

📂 Folder Structure & Contents

The application folder contains:
• PowerWatchUA.exe — The main executables (WinForms, .NET Framework 4.x).
• PowerWatchUA.cs — Source code file (only needed if you plan to recompile the app).
• PowerWatch_REPORT.txt — Full report containing development notes, tests, and recommendations.
⚠️ Important: The folder itself can be moved anywhere because the program uses absolute paths. However, do not move system dependencies.

⚙️ How It Works


🚀 Startup & Auto-run

• The application runs on system startup via a shortcut (PowerWatch.lnk) pointing to PowerWatchUA.exe.
• Shortcut location: %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup

💾 Data & Configuration

Application data is stored in the user profile (%USERPROFILE%\) rather than the program folder:
• powerwatch_config.json — Configuration file (Telegram channel name, queue/group, and time buffer in minutes).
• powerwatch_state.json — Current state tracker (last check timestamp, current blackout plan).
• powerwatch.log — Application logs and activity journal.
• powerwatch_pause — A temporary file created/deleted when the "Pause" button is toggled.

🔒 Single Instance & Localization

• Only one instance of the program can run at a time (controlled via a system Mutex).
• The RU (PowerWatch.exe) and UA (PowerWatchUA.exe) versions share the same configuration files and will not conflict, but they cannot run simultaneously.

🛠️ System Dependencies

The program relies on built-in Windows utilities. Do not move or modify them:
• shutdown.exe — Executes the system shutdown.
• WM_CLOSE — Sent to active applications to safely save user data before power-off.

💻 Recompiling from Source

If you modify PowerWatchUA.cs, you can recompile it using the built-in .NET compiler.
⚠️ Note: Close the running PowerWatchUA.exe before compiling, otherwise the file will be locked.
Run the following command in Command Prompt (adjust paths if necessary):
cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /out:"C:\Users\user\Desktop\PowerWatchUA\PowerWatchUA.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Management.dll "C:\Users\user\Desktop\PowerWatchUA\PowerWatchUA.cs"
Используйте код с осторожностью.

🧪 Test Modes (No Actual Shutdown)

You can test the application features safely without powering off your PC:
• PowerWatchUA.exe /test — Validates the Telegram schedule parser and writes the results to the log.
• PowerWatchUA.exe /testui — Launches the countdown window with the cancellation button WITHOUT triggering shutdown.exe.

🚚 Moving to Another Laptop

The application folder is fully portable. PowerWatchUA.exe uses .NET Framework 4.x, which is pre-installed on Windows 10 and 11. No external configuration files are required for a clean install.
1. First Launch: On a new laptop, the program initializes with default values (Channel: SvitloKropyvnytskyiMisto, Queue/Group: 4.1, Buffer: 15 min).
2. Data Regeneration: Logs, config, and state files will be automatically created in the new user's %USERPROFILE%.
3. Fixing Startup: The old startup shortcut points to the previous user path. On the new laptop:
	• Right-click PowerWatchUA.exe -> Send to -> Desktop (create shortcut).
	• Move this new shortcut into %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup.
🌍 Time Zone Warning: The schedule is parsed from a local Ukrainian Telegram channel. If the laptop is moved to a different time zone, the shutdown timer will still sync with the channel's local time, not the laptop's current clock.

🚨 Critical Behavior & Emergency Stop

• Data Safety: The program automatically requests all open applications to close gracefully (WM_CLOSE), ensuring your work is saved before initiating a system shutdown.
• Grace Period: The laptop will shut down after a 3-minute and 30-second countdown.
• Aborting Shutdown: Click the big red "СКАСУВАТИ ВИМКНЕННЯ" (CANCEL SHUTDOWN) button in the countdown window to abort.

## ☕ Support the project (Crypto)

If this tool helps you stay organized and protects your data during blackouts, you can support the development via crypto:

* **USDT (TRC-20):** `TMPhdnKSesRhe6dAeHb6X6uLcUshvb2Hhj` *(Min. deposit: 1 USDT)*
* **USDT (BEP20):** `0xaee7ecdd8aeff38b8904471e809f531d9b3bffcf` *(Min. deposit: 0.1 USDT)*
* **TON:** `UQDu3cLECCN1HERQ1Y7mRhFU5Y5bko3Wz34q016KbEhAtZNh` *(Min. deposit: 0.1 TON — **No Memo/Tag required!**)*

Thank you for your support! 💙💛

Українська версія

PowerWatchUA — це утиліта для Windows, призначена для автоматичного вимкнення ноутбука або ПК відповідно до графіків відключень світла в Україні. Програма моніторить визначені Telegram-канали, відлічує час до вимкнення та безпечно завершує роботу системи.

📂 Склад папки PowerWatchUA

У робочій директорії містяться:
• PowerWatchUA.exe — основна робоча програма (WinForms, .NET Framework 4.x).
• PowerWatchUA.cs — вихідний код (потрібен лише для перезбирання утиліти).
• PowerWatch_REPORT.txt — повний звіт про виконану роботу, тести та рекомендації.
⚠️ Важливо: Саму папку можна вільно переносити, оскільки всі шляхи у програмі є абсолютними. Проте системні залежності поза папкою чіпати не можна.

⚙️ Як це працює


🚀 Автозапуск

• Програма запускається разом із системою через ярлик (PowerWatch.lnk), який посилається на PowerWatchUA.exe.
• Шлях до папки автозапуску: %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup

💾 Дані та конфігурація

Усі робочі дані зберігаються НЕ в папці програми, а в профілі користувача (%USERPROFILE%\):
• powerwatch_config.json — налаштування (назва каналу, черга/група, запас хвилин).
• powerwatch_state.json — поточний стан (останнє опитування, план вимкнення).
• powerwatch.log — журнал подій (лог).
• powerwatch_pause — тимчасовий файл паузи (створюється/видаляється кнопкою «Пауза»).

🔒 Робота в один екземпляр та локалізації

• Другий екземпляр програми не запуститься завдяки системному Mutex.
• Російська (PowerWatch.exe) та українська (PowerWatchUA.exe) версії використовують спільні файли даних у профілі й не конфліктують між собою, але одночасно працювати не можуть.

🛠️ Системні залежності

Програма використовує штатні засоби Windows, які не переносяться:
• shutdown.exe — для вимкнення ПК.
• WM_CLOSE — для коректного закриття програм із збереженням даних користувача.

💻 Перезбирання з вихідного коду

Якщо ви внесли зміни у файл PowerWatchUA.cs, його можна перекомпілювати за допомогою вбудованого в Windows компилятора .NET.
⚠️ Важливо: Перед перезбиранням обов'язково закрийте працюючий PowerWatchUA.exe, інакше файл буде заблоковано системою.
Виконайте команду в командному рядку (cmd):
cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /out:"C:\Users\user\Desktop\PowerWatchUA\PowerWatchUA.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Management.dll "C:\Users\user\Desktop\PowerWatchUA\PowerWatchUA.cs"
Используйте код с осторожностью.

🧪 Тестові режими (без вимкнення ПК)

Для перевірки функціоналу передбачені безпечні режими запуску:
• PowerWatchUA.exe /test — перевірка парсера розкладу та запис результатів у журнал.
• PowerWatchUA.exe /testui — відображення вікна відліку часу з кнопкою скасування БЕЗ виклику команди shutdown.

🚚 Перенос на інший ноутбук

Папка є повністю самодостатньою для роботи. Програма написана під .NET Framework 4.x, який за замовчуванням вбудований у Windows 10 та 11, тому додаткові налаштування системи не потрібні.
1. Перший запуск: На новому ноутбуці програма застосує стандартні значення (канал: SvitloKropyvnytskyiMisto, Черга: 4.1, запас часу: 15 хв).
2. Регенерація даних: Журнал, конфіг та стан створяться автоматично в %USERPROFILE% нового користувача.
3. Оновлення автозапуску: Старий ярлик прив'язаний до шляхів попереднього ПК. На новому ноутбуці:
	• Клацніть правою кнопкою миші на PowerWatchUA.exe -> Надіслати -> Робочий стіл (створити ярлик).
	• Перемістіть створений ярлик у папку %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup.
🌍 Важливо щодо часових поясів: Розклад підтягується з конкретного Telegram-каналу під місцевий час міста. Якщо ноутбук опиниться в іншому часовому поясі, вимкнення все одно вираховуватиметься за часом каналу, а не за локальним годинником пристрою.

🚨 Критична поведінка та скасування дії

• Збереження даних: Програма самостійно надсилає команду WM_CLOSE усім відкритим додаткам, що дозволяє їм зберегти ваші дані перед вимкненням.
• Таймаут: Вимкнення ноутбука відбувається після зворотного відліку тривалістю 3 хвилини 30 секунд.
• Скасування: Для зупинки вимкнення натисніть велику червону кнопку «СКАСУВАТИ ВИМКНЕННЯ» у вікні відліку.

## ☕ Підтримати проєкт (Crypto)

Якщо эта утиліта допомагає вам організувати роботу та зберігає ваші дані під час відключень світла, ви можете підтримати розробку за допомогою криптовалюти:

* **USDT (TRC-20):** `TMPhdnKSesRhe6dAeHb6X6uLcUshvb2Hhj` *(Мін. депозит: 1 USDT)*
* **USDT (BEP20):** `0xaee7ecdd8aeff38b8904471e809f531d9b3bffcf` *(Мін. депозит: 0.1 USDT)*
* **TON:** `UQDu3cLECCN1HERQ1Y7mRhFU5Y5bko3Wz34q016KbEhAtZNh` *(Мін. депозит: 0.1 TON — **Memo/Tag НЕ потрібен!**)*

Дякую за вашу підтримку! 💙💛


