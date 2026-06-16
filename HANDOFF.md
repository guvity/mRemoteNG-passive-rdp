# HANDOFF — Passive RDP Monitor (ветка v4)

> Живой документ для передачи работы между чатами/сессиями. Обновляется после каждой
> завершённой стадии. Если контекст переполнился — новый чат начинает с раздела
> **«Как продолжить»**, не переисследуя код заново.

---

## 0. Метаданные

| Поле | Значение |
|---|---|
| Репозиторий | https://github.com/guvity/mRemoteNG-passive-rdp |
| Рабочая ветка | `passive-rdp-monitor-1772-v4` (создана от `passive-rdp-monitor-1772-v3`) |
| Базовая ветка апстрима | `v1.78.2-dev` (merge-base `aee497de`) |
| Тип сборки | **только Release Portable / x64** (define `PORTABLE` → всё рядом с exe) |
| Целевой фреймворк | `net6.0-windows` (на сервере должен стоять .NET 6 Desktop Runtime x64) |
| Язык общения с пользователем | **русский** (код/идентификаторы — как есть) |
| Правило сборки | Код правим по стадиям; **собираем только в самом конце** (Фаза C). В GitHub не пушим и Actions не запускаем без явной команды пользователя. CI на ветке v4 не триггерится (workflow слушает только `...-v2`). |

---

## 1. Цель и контекст

mRemoteNG в режиме «пассивного мониторинга» RDP: окно показывает удалённый экран,
ввод заблокирован (View Only), при обрыве — авто-reconnect, экран проматывается в
правый нижний угол. Предыдущий агент (ветки v1→v3) реализовал основу. Эта ветка (v4)
**исправляет недочёты** и добавляет UI-фичи.

### Что уже работает (НЕ ломать)
- Выход из fullscreen: окно сворачивается до видимой области, скролл в правый нижний
  угол, включается View Only, **мышь/клавиатура освобождаются** (это эталон поведения!).
- Оконный режим: можно скроллить вручную; при потере фокуса возврат в правый нижний угол.
- В **fullscreen с включённым VO** — видно экран, мышь/клавиатура НЕ уходят в сессию;
  с выключенным VO — полное управление. (Управляется вручную — сохранить.)

### Что НЕ работает / надо доделать
- **При reconnect мышь «летает» по окну** (курсор двигается сам, хотя не кликает). При
  массовом reconnect — невозможно найти и «успокоить» окно. **Это приоритет №1.**
- Вход в fullscreen сейчас **форсит VO** (а должен сохранять состояние) — отсюда «иногда
  VO включается, когда работаю» и обратные эффекты.
- При **auto-reconnect по таймауту** НЕ применяются performance-настройки → композиция/тени.
- При **переключении вкладок** не гарантируются scroll-в-угол + VO ВКЛ.

### Целевая модель поведения (ИТОГ — чек-лист пользователя, ЭТАЛОН)
1. **Первый коннект** → fullscreen, **ViewOnly СНЯТ** (VO по дефолту выкл → работа на
   рабочем столе), connection bar сдвинут в **правый верхний угол** (размер НЕ менять).
2. **Вход в fullscreen НЕ меняет VO** — сохраняется текущее состояние: было VO вкл
   (оконный мониторинг) → в fullscreen тоже вкл (вижу, ввод не уходит в сессию); было
   выкл → управляю. **В fullscreen VO переключается вручную.** Вход НЕ форсит VO.
3. **Выход из fullscreen** (ВСЕГДА) → скролл в правый нижний угол + **ViewOnly принудительно ВКЛ**.
4. **Reconnect** (любой) → **ViewOnly соблюдается, мышь/клавиатура НЕ перехватываются**.
5. **Переключение вкладок** (клик по вкладке ИЛИ Ctrl+Tab) на оконную пассивную RDP-вкладку
   → проверить скролл (если не в правом нижнем — довести) + **ViewOnly ВКЛ**. Вкладку в
   fullscreen не трогаем.
6. **Fullscreen + VO снят** = полный доступ; **Fullscreen + VO активен** = только просмотр.

---

## 2. Корневой диагноз (САМОЕ ВАЖНОЕ)

«View Only» реализован НЕ родным RDP API (его нет у `MsRdpClient`), а перехватом
Windows-сообщений: `PassiveRdpInputBlocker` = `IMessageFilter` (очередь сообщений
приложения) + сабклассинг дочерних окон ActiveX-контрола (`BlockedWindow.WndProc`).
Файл: `mRemoteNG/Connection/Protocol/RDP/RdpInputBlocker.cs`.

**Почему мышь «летает» при reconnect:**

1. Есть рабочий механизм освобождения захвата мыши — `FinalizeRdpFullscreenExitOnce`
   (`RdpProtocol6.cs`): `GetCapture` → `SendCancelModeToRdpWindows` (WM_CANCELMODE +
   WM_KILLFOCUS) → **`ReleaseCapture()`** → **`ClipCursor(IntPtr.Zero)`** +
   `Cursor.Clip=Empty` → перевод фокуса в фиктивный 1×1 `PassiveRdpFocusSink`.
   НО он вызывается **только при выходе из fullscreen** (`StartFullscreenExitFinalizer`).
2. При reconnect (`RDPEvent_OnAutoReconnected`) этот финализатор **НЕ вызывается** —
   только scroll + ViewOnly. Поэтому mstscax удерживает mouse capture / ClipCursor, а
   `PassiveRdpInputBlocker` ест только оконные сообщения (клики глушит — поэтому «не
   кликает»), но capture и позиционирование курсора идут мимо него → курсор «летает».
3. Сабкласс-блокировка устаревает: при reconnect mstscax пересоздаёт внутренние дочерние
   окна (новые HWND), а переустановка сабкласса на reconnect ненадёжна. Поэтому ручной
   toggle View Only «чинит» (он пере-сабклассит новые окна через Unblock→Block).
4. `ApplyFullscreenViewOnlyPolicy` включает VO только если `IsFullscreenEffective()`; в
   оконном reconnect VO зависит от успеха scroll, а `_autoEnableViewOnlyAfterSuccessfulScroll`
   гасится после первого успеха.

**Вывод/решение:** «reconnect-финализатор» по аналогии с fullscreen-exit — переиспользовать
release-capture блок и запускать таймером (несколько попыток, т.к. mstscax восстанавливает
capture асинхронно) во всех путях reconnect.

**Политика VO в fullscreen (по итоговой модели):** сейчас `ApplyFullscreenViewOnlyPolicy`
ФОРСИТ VO в fullscreen, а `MarkRdpFullscreenActive(true)` сбрасывает
`_userManuallyDisabledViewOnly` — это нарушает требование «вход в fullscreen не меняет VO».
Надо: при входе НЕ трогать VO (сохранить состояние); при выходе — форсить VO ВКЛ + scroll.
Это устраняет и баг «иногда VO при первом коннекте».

**Переключение вкладок:** `NotifyPassiveTabActivated` сейчас при `!ViewOnly` делает ранний
`return` (scroll не доводит, VO не включает). Надо: для оконной пассивной RDP-вкладки
довести scroll в правый нижний + форсить VO ВКЛ (вкладку в fullscreen не трогать).

**Performance-флаги при reconnect (подтверждено проверкой):** `SetPerformanceFlags`
(`_rdpClient.AdvancedSettings2.PerformanceFlags`) вызывается **только в `Initialize`**.
- Меню **Reconnect** и **Reconnect All** пересоздают сессию (`Close()` + `OpenConnection`) →
  настройки применяются заново. ✅
- **Auto-reconnect по таймауту** (`OnAutoReconnecting`/`OnAutoReconnected`) делает reconnect
  **внутри mstscax**, mRemoteNG не вызывает `SetPerformanceFlags` → возможны композиция/тени. ❌
- `tmrReconnect` (`_rdpClient.Connect()`) и RDP8 `ReconnectForResize` тоже не переустанавливают pFlags.
→ Переустанавливать pFlags во всех путях reconnect (A5).

---

## 3. Карта кода (ориентир — по именам методов; номера строк могут сдвигаться)

### `mRemoteNG/Connection/Protocol/RDP/RdpProtocol6.cs` (ядро)
- Поля состояния: `InputBlocker` (static, общий на все сессии!), `_viewOnly`,
  `_automaticReconnectInProgress`, `_suppressFocusOnAutomaticReconnect`,
  `_isRdpFullscreenActive`, `_fullscreenRequestedByMRemote`, `_userManuallyDisabledViewOnly`,
  `_autoEnableViewOnlyAfterSuccessfulScroll`, `_hasCompletedInitialConnect`.
- `SetViewOnly(value, source)` — `_viewOnly=value` + `InputBlocker.SetBlocked(Control, _viewOnly)`. (НЕ трогает родной RDP.)
- `FinalizeRdpFullscreenExitOnce(source)` — **эталон освобождения захвата** (ReleaseCapture/ClipCursor/CancelMode/focus sink).
- `StartFullscreenExitFinalizer` / `FullscreenExitFinalizeTimerOnTick` — таймер 15×100мс.
- `SendCancelModeToRdpWindows` — WM_CANCELMODE+WM_KILLFOCUS всем дочерним окнам.
- `ApplyFullscreenViewOnlyPolicy` — СЕЙЧАС форсит VO в fullscreen (по A4: НЕ форсить при входе).
- `MarkRdpFullscreenActive` — при входе сбрасывает `_userManuallyDisabledViewOnly` (по A4: не сбрасывать).
- `NotifyPassiveTabActivated` — обработка активации вкладки (по A4: убрать ранний `return` по `!ViewOnly`, форсить scroll+VO для оконной пассивной вкладки).
- `BeginAutomaticReconnect` / `EndAutomaticReconnect` / `ClearAutomaticReconnectState`.
- `ShouldSuppressRdpFocus` = `ViewOnly || _automaticReconnectInProgress || _suppressFocusOnAutomaticReconnect`.
- `ScrollToLowerRight` → при успехе → `EnableViewOnlyAfterSuccessfulPassiveLayout` (гасит auto-флаг).
- `SetPerformanceFlags()` — пишет `AdvancedSettings2.PerformanceFlags` из `connectionInfo`. Вызывается из `SetRdpClientProperties` в `Initialize`.
- `SetRdpClientProperties()` — `EnableAutoReconnect=true`, `MaxReconnectAttempts=RdpReconnectionCount`.
- События: `RDPEvent_OnConnected`, `RDPEvent_OnLoginComplete` (initial),
  `RDPEvent_OnAutoReconnecting` / `RDPEvent_OnAutoReconnected` (ActiveX reconnect),
  `tmrReconnect_Elapsed` (mRemoteNG reconnect), `OnEnter/OnLeave/OnRequestGo/OnRequestLeaveFullscreen`.

### `mRemoteNG/Connection/Protocol/RDP/RdpProtocol8.cs` (наследник RdpProtocol7→6)
- `ReconnectForResize` / `DoResize` — третий путь reconnect (RdpClient8.Reconnect по resize). Без release-capture и без pFlags.

### `mRemoteNG/Connection/Protocol/RDP/RdpInputBlocker.cs`
- `PassiveRdpInputBlocker.SetBlocked(control, blocked)` → `Block`/`Unblock`.
- `Block` → `RefreshSubclassedWindows` + `ScheduleRefreshRetries` (8×200мс).
- `BlockedWindow.WndProc` — ест input-сообщения (для WM_MOUSEACTIVATE возвращает MA_NOACTIVATEANDEAT).

### `mRemoteNG/UI/Window/ConnectionWindow.cs` + `.Designer.cs` (меню вкладки)
- Меню `cmenTab` (Designer): `cmenTabReconnect`, `cmenTabFullscreen`, `cmenTabSmartSize`,
  `cmenTabViewOnly` и др. Размер пунктов 230×22.
- Обработчики (.cs): `cmenTabFullscreen.Click → ToggleFullscreen()`,
  `cmenTabViewOnly.Click → ToggleViewOnly()`, `cmenTabReconnect.Click → Reconnect()`.
- `Reconnect()` = `Prot_Event_Closed(...)` + `OpenConnection(Info, DoNotJump)` (re-open).
- `reconnectAll(initiator)` = в цикле `Protocol.Close()` + `OpenConnection(Info, DoNotJump)`.
- Активация вкладки (`connDock.ActiveContentChanged`) → `rdp.NotifyPassiveTabActivated()`.

### `mRemoteNG/UI/Menu/ViewMenu.cs`
- `mMenReconnectAll_Click` → перебор окон → `connectionWindow.reconnectAll(...)`.

### `mRemoteNG/UI/Forms/frmMain.cs` (+ `.Designer.cs`) — главная форма
- Сюда Ctrl+Tab / Ctrl+Shift+Tab (ProcessCmdKey). DockPanel = `pnlDock`.
- После переключения дергать проверку scroll+VO (общий механизм с `NotifyPassiveTabActivated`).
- Существующие хоткеи: Ctrl+N/Ctrl+O (FileMenu), F11 (Fullscreen, ViewMenu), Ctrl+S и т.д.

### Portable
- `App/Runtime.cs`: `IsPortableEdition` через `#if PORTABLE`.
- `App/Info/SettingsFileInfo.cs`: в portable `SettingsPath = ExePath` (рядом с exe).
- `mRemoteNG.csproj`: `Release Portable|x64` задаёт `DefineConstants=PORTABLE`.

### Connection bar («козырёк»)
- В коде **не настраивается** — системный RDP connection bar (mstscax), по умолчанию
  вверху по центру. Перемещение требует поиска HWND и `SetWindowPos` (риск). Размер НЕ менять.

---

## 4. Согласованные с пользователем решения

1. **Модель ViewOnly/Fullscreen (ИТОГ — см. раздел 1):**
   - **Вход в fullscreen НЕ меняет VO** (сохранить состояние; НЕ форсить). Первый коннект:
     VO по дефолту выкл → работа. VO в fullscreen — вручную.
   - **Выход из fullscreen** → ViewOnly **принудительно ВКЛ** + скролл в правый нижний угол.
   - **Reconnect** → ViewOnly **ВКЛ**, ввод не перехватывается.
   - **Переключение вкладок** (клик/Ctrl+Tab) на оконную пассивную вкладку → довести скролл
     в правый нижний + **VO ВКЛ** (вкладку в fullscreen не трогать).
   - Правки: `ApplyFullscreenViewOnlyPolicy` (убрать форс VO в fullscreen),
     `MarkRdpFullscreenActive` (не сбрасывать `_userManuallyDisabledViewOnly`),
     `NotifyPassiveTabActivated` (убрать ранний `return` по `!ViewOnly`). Всё в A4.
2. **Performance-настройки при reconnect** — auto-reconnect по таймауту (mstscax internal)
   НЕ переустанавливает `PerformanceFlags` → композиция/тени. Переустанавливать pFlags во
   всех путях reconnect (A5). Меню Reconnect / Reconnect All — уже применяют (пересоздание).
3. **Фикс захвата мыши при reconnect** — reconnect-финализатор (переиспользовать логику
   выхода из fullscreen). Без копания в родном API mstscax.
4. **Верификация** — между стадиями строгий ревью; .NET 6 SDK ставим только перед Фазой C.
5. **Только portable**, все конфиги/логи/подключения — рядом с exe.
6. **Переключение вкладок** — `Ctrl+Tab` (следующая) / `Ctrl+Shift+Tab` (предыдущая). Ctrl+N НЕ трогаем.
7. **Порядок работы** — сначала Фаза A (фиксы), затем Фаза B (косметика), затем C (сборка).
8. **Connection bar** — двигать в правый верхний угол при входе в fullscreen, **размер НЕ менять**.

---

## 5. Чек-лист стадий

Статусы: `[ ]` не начато · `[~]` в работе · `[x]` готово (указывать хэш коммита).

### Фаза A — критичные фиксы поведения
- [x] **A1** — этот HANDOFF.md (диагноз, карта кода, чек-лист). *Без кода.* (коммит `84b05339`)
- [x] **A2** — Reconnect-финализатор: выделен `ReleaseRdpInputCaptureOnce`, добавлен таймерный
  `StartReconnectInputFinalizer` (20×150мс), вызывается из `RDPEvent_OnAutoReconnected`;
  в fullscreen не вмешивается. `FinalizeRdpFullscreenExitOnce` отрефакторен на общий метод.
- [x] **A3** — Надёжная переустановка блокировки: `PassiveRdpInputBlocker.Rebind` (release+
  refresh сабклассов) + в reconnect-финализаторе форс VO и `Rebind` каждый тик (покрывает
  позднее пересоздание окон mstscax).
- [x] **A4** — Политика ViewOnly по чек-листу. `MarkRdpFullscreenActive`: сброс VO-флагов
  перенесён со входа на выход (вход НЕ меняет VO). `ApplyFullscreenViewOnlyPolicy`: убран
  форс VO в fullscreen. `NotifyPassiveTabActivated`: форс VO при активации оконной вкладки.
  `EnableViewOnlyAfterSuccessfulPassiveLayout`: guard против VO в fullscreen (гонка 1-го коннекта).
  Выход форсит VO (существующий «fullscreen leave input shield» + scroll).
- [ ] **A5** — Унификация всех путей reconnect: release-capture финализатор + VO policy +
  **переустановка performance-настроек (pFlags)**. Покрыть ActiveX auto-reconnect (по
  таймауту НЕ применяет настройки!), tmrReconnect, RDP8 resize, меню Reconnect/Reconnect All.

### Фаза B — UI/фичи
- [ ] **B1** — Пункты Fullscreen и ViewOnly в меню вкладки крупнее ×2 (Font, позиции не менять).
- [ ] **B2** — Новый пункт под ViewOnly: «снять ViewOnly + открыть Fullscreen» (активный режим, подавляет авто-VO).
- [ ] **B3** — Проверка семантики performance-флагов (композиция/тени) на реальном конфиге.
  Меню Reconnect/Reconnect All уже пересоздают сессию. Основная переустановка pFlags — в A5.
- [ ] **B4** — Горячие клавиши Ctrl+Tab / Ctrl+Shift+Tab (frmMain, pnlDock). После
  переключения — проверка scroll+VO (общий механизм с `NotifyPassiveTabActivated`, A4).
- [ ] **B5** — RDP connection bar в правый верхний угол при входе в fullscreen (нужен уже
  на первом коннекте). **Размер НЕ менять, только позиция.** *РИСК — runtime-подбор HWND.*

### Фаза C — сборка и проверка
- [ ] **C0** — Установить .NET 6 SDK (winget) — только перед сборкой.
- [ ] **C1** — Собрать Release Portable/x64 → исправить ошибки → portable Zip → проверить запуск.

---

## 6. Как продолжить (для нового чата)

1. `git clone https://github.com/guvity/mRemoteNG-passive-rdp.git` (если ещё нет локально).
2. `cd mRemoteNG-passive-rdp && git checkout passive-rdp-monitor-1772-v4`.
   - Если ветки v4 нет на remote (не пушили) — она локальная; продолжать в локальной.
3. Прочитать этот `HANDOFF.md` целиком (особенно разделы 1, 2, 4).
4. Найти первую незавершённую стадию в чек-листе (раздел 5) и продолжить с неё.
5. Каждая стадия = атомарный коммит, оставляющий код компилируемым. После стадии —
   обновить чек-лист (отметить `[x]` + хэш) и закоммитить.
6. Сборку НЕ запускать до Фазы C. В GitHub не пушить без команды пользователя.
7. Все ответы пользователю — на русском.

---

## 7. Открытые риски / заметки

- **B5 (connection bar)** — самый рискованный пункт: mstscax не даёт API для позиции
  connection bar. Возможно EnumWindows по классу окна + SetWindowPos с повтором (RDP может
  пересоздавать бар). Размер НЕ менять. Если не выйдет надёжно — согласовать запасной вариант.
- **B4 (Ctrl+Tab)** — проверить, не перехватывает ли Ctrl+Tab сам RDP ActiveX, когда у
  него фокус (при активной работе с VO выкл).
- **A5/B3 (performance flags)** — при auto-reconnect переустанавливать pFlags; уточнить
  семантику EnableDesktopComposition/DisableCursorShadow на реальном конфиге сессии.
- **A4 (политика VO)** — затрагивает `ApplyFullscreenViewOnlyPolicy`, `MarkRdpFullscreenActive`,
  `NotifyPassiveTabActivated`, обработчики OnEnter/OnLeaveFullscreen. Тщательно, чтобы не
  сломать уже работающий выход из fullscreen и fullscreen+VO (ручной просмотр).
- `InputBlocker` — **static**, общий на все сессии; учитывать при массовых reconnect.
- Сборка `Release Portable` тянет postbuild PowerShell-скрипт (подпись) — для локальной
  сборки портабла подпись можно пропустить.

---

_Журнал изменений HANDOFF:_
- _A1 — создан документ (диагноз + карта + чек-лист)._
- _A1.1 — уточнена модель VO по чек-листу (fullscreen=работа); добавлен фикс
  performance-флагов при auto-reconnect (A5); B3 сведён к проверке семантики флагов._
- _A1.2 — уточнено: вход в fullscreen НЕ меняет VO (сохраняет состояние), а не форсит;
  добавлена проверка scroll+VO при переключении вкладок (клик/Ctrl+Tab) — правки в
  `NotifyPassiveTabActivated`; connection bar — размер не менять._
- _A2 — реализован reconnect-финализатор (release-capture при OnAutoReconnected);
  `FinalizeRdpFullscreenExitOnce` отрефакторен на общий `ReleaseRdpInputCaptureOnce`._
- _A3 — добавлен `InputBlocker.Rebind` (полный re-subclass); reconnect-финализатор форсит
  VO + Rebind на пересозданные окна mstscax._
- _A4 — политика VO по чек-листу: вход в fullscreen сохраняет VO (не форсит), выход форсит VO,
  активация вкладки форсит VO, scroll не включает VO в fullscreen (устранена гонка)._
