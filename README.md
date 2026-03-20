# windows-notification-mover

Windows 11 のトースト通知を右下から右上に自動移動するツール。

## 概要

Windows 11 の通知はデフォルトで右下に表示されるが、設定で変更する手段がない。本ツールは WinEvent フックを使って通知ウィンドウの出現を検知し、右上に移動する。常駐プロセスだがアイドル時の CPU 使用率はほぼ 0%。

## 必要環境

- Windows 11
- .NET Framework 4.x（Windows に標準搭載）
- .NET SDK 不要

## セットアップ

### 1. ビルド

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /out:NotificationMover.exe ^
  /target:winexe ^
  NotificationMover.cs
```

### 2. 起動・自動登録

`NotificationMover.exe` をダブルクリックして起動する。初回起動時に自動でタスクスケジューラへ登録され、次回ログオン以降は自動起動する。

## 起動・停止

```powershell
# 起動
Start-ScheduledTask -TaskName 'NotificationMover'

# 停止
Stop-Process -Name NotificationMover -Force

# スタートアップ登録を解除
Unregister-ScheduledTask -TaskName 'NotificationMover' -Confirm:$false
```

## カスタマイズ

`NotificationMover.cs` 内の以下の値を変更してビルドし直す。

### 表示位置のマージン

```csharp
int margin = 10;  // 画面端からの距離（ピクセル）
```

### 移動ループの設定

通知アニメーションに対抗して繰り返し移動する回数と間隔。

```csharp
for (int i = 0; i < 20; i++)   // 繰り返し回数（20回 × 30ms = 600ms）
{
    ...
    Thread.Sleep(30);            // 移動間隔（ms）
}
```

## デバッグ

通知ウィンドウの検出状況をファイルに記録する。

```powershell
.\NotificationMover.exe --debug
```

ログは `debug.log` に出力される。

```
17:18:16.908 Started. Screen=2880x1800
17:18:19.683 [MATCH] ev=32779 cls=Windows.UI.Core.CoreWindow proc=ShellExperienceHost pos=(2088,1704) size=792x86
17:18:19.749 [MOVE] hwnd=67236 -> (2078,10)
```

## トラブルシューティング

### 通知が移動しない

`--debug` モードで起動し、通知を発生させて `debug.log` を確認する。

- `[MATCH]` が出ない → 通知ウィンドウを検出できていない。Windows アップデートでプロセス名・クラス名が変わった可能性がある。ログ内の `ShellExperienceHost` + `Windows.UI.Core.CoreWindow` の組み合わせを確認し、`IsNotificationWindow` メソッドを修正してビルドし直す。
- `[MATCH]` は出るが移動しない → 移動ループの回数・間隔を増やす。

### Windows アップデート後に動作しなくなった

`--debug` で起動し通知を出して `debug.log` を確認。通知ウィンドウの `cls`（クラス名）と `proc`（プロセス名）を特定し、`IsNotificationWindow` を修正する。

```csharp
static bool IsNotificationWindow(string cls, string procName)
{
    return procName == "ShellExperienceHost" && cls == "Windows.UI.Core.CoreWindow";
}
```

## 仕組み

1. `SetWinEventHook` で `EVENT_OBJECT_SHOW` / `EVENT_OBJECT_LOCATIONCHANGE` を監視
2. `ShellExperienceHost` の `Windows.UI.Core.CoreWindow` が画面右下に出現したら通知ウィンドウと判定
3. `SetWindowPos` で右上に移動。通知アニメーションが位置を上書きするため 30ms ごとに 20 回繰り返す
4. 一度移動したウィンドウは `alreadyMoved` で追跡し、閉じるアニメーションで再移動しないよう制御
