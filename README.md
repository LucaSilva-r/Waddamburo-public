<p align="center"><img src="eng/packaging/waddamburo.svg" alt="Waddamburo logo" width="128"></p>
<h1 align="center">Waddamburo</h1>
<p align="center"><i>Relive the Gen3 Taiko experience on Windows and Linux.<br>WindowsとLinuxで、AC15時代の太鼓体験をもう一度。</i></p>
<h2 align="center"><a href="#日本語ガイド">日本語ガイド</a></h2>
<p align="center"><a href="https://github.com/LucaSilva-r/Waddamburo-public/releases">Download</a> · <a href="#getting-started">Setup</a> · <a href="#basic-controls">Controls</a> · <a href="#play-online-with-taikonline">Online</a> · <a href="BUILDING.md">Build from source</a></p>

<h3 align="center">Waddamburo is an independent, open-source Taiko no Tatsujin simulator.</h3>

<p align="center"><strong>NOT A RECOMPILATION OR A MOD!!</strong></p>

**Waddamburo uses a completely new, independently developed engine. No Bandai
Namco source code or compiled game code is included in Waddamburo.**

It brings the **Gen3 arcade experience (2011–2019)** to your PC, with:

- **Online scores and rankings** through [TaikOnline](https://taikonline.com).
- Uncapped FPS and upscaled graphics.
- One- and two-player support, custom drum bindings, and extra song libraries.

**You need legitimately obtained original game files. Game data and commercial
fonts are not included.**

## Getting started

1. Download `Waddamburo.exe` for Windows x64 or `Waddamburo-x86_64.AppImage` for
   Linux x64 from [Releases](https://github.com/LucaSilva-r/Waddamburo-public/releases).
2. Open your legitimately obtained game data folder for a [compatible version](#compatible-gen3-arcade-versions)
   and drag Waddamburo into the **`USRDIR` folder**, next to the `data` folder.

   <a name="compatible-gen3-arcade-versions"></a>

   **Compatible Gen3 arcade versions**

   | Year | Arcade version |
   | --- | --- |
   | 2012 | Taiko no Tatsujin KATSU-DON |
   | 2013 | Taiko no Tatsujin Sorairo Ver. |
   | 2013 | Taiko no Tatsujin Momoiro Ver. |
   | 2014 | Taiko no Tatsujin Kimidori Ver. |
   | 2015 | Taiko no Tatsujin Murasaki Ver. |
   | 2015 | Taiko no Tatsujin White Ver. |
   | 2016 | Taiko no Tatsujin Red Ver. |
   | 2017 | Taiko no Tatsujin Yellow Ver. |
   | 2018 | Taiko no Tatsujin Blue Ver. |
   | **2019** | **Taiko no Tatsujin Green Ver.** |

   **Development and testing currently focus on Green Ver.** Behavior on other
   versions is unverified.

3. To use the original font, place your legitimately obtained **DFPKanTeiRyu-XB**
   at **`USRDIR/data/font/font.ttf`**. Create the `font` folder if needed.
4. Launch Waddamburo, choose your player account or **Guest**, and play!

On Windows, the font path is `USRDIR\data\font\font.ttf`. Restart after adding it.

<details>
<summary>First launch on Linux</summary>

Make the AppImage executable through your file manager's permissions, or run these
commands inside `USRDIR`:

```sh
chmod +x Waddamburo-x86_64.AppImage
./Waddamburo-x86_64.AppImage
```

</details>

## Play online with TaikOnline

**Sign in to save scores to your account and see online rankings in Song Select.**

In Song Select, highlight a song and press **L** to open its rankings (single-player).

1. On the player-select screen, choose **Sign in**.
2. Scan the QR code, or enter the code shown by the game on [TaikOnline](https://taikonline.com).
3. Return to the game, select your account, and start playing. It will be remembered
   on this PC for your next session.

Each player can choose their own account in two-player mode. On a friend's PC,
choose **Join with code** to use your account without saving the login there.

Online is optional: choose **Guest** to play without signing in. You can also play offline.

## Basic controls

| Drum hit | Player 1 | Player 2 |
| --- | --- | --- |
| Left rim — Ka | **D** | **Z** |
| Left centre — Don | **F** | **X** |
| Right centre — Don | **J** | **C** |
| Right rim — Ka | **K** | **V** |

**In menus, hit a rim to move and the centre to confirm.** Arrow keys and Enter also work.
The missing player can join from Song Select by hitting their drum.

| Shortcut | Action |
| --- | --- |
| **Escape** | Open the pause menu and Settings |
| **F11** | Toggle fullscreen |
| **Space** | Skip startup screens |
| **L** | Open or close the selected song's rankings in Song Select (single-player) |
| **Hold Q** | Restart during a song or its results |

To change keys or set up a controller or MIDI drum, open **Escape → Settings → Drum Controls**.

## Make it your own

Open **Escape → Settings** to change language, volume, graphics, controls, and timing.

- **Extra songs:** put TJA charts in `USRDIR/custom_songs`, or choose your TJA,
  osu!lazer, or Nijiiro library in **Settings → Songs**.

For build instructions on Windows and Linux, tests, release packaging, and
contributions, see the [Build and development guide](BUILDING.md).

## 日本語ガイド

<h3 align="center">Waddamburoは、独立して開発されたオープンソースの「太鼓の達人」シミュレーターです。</h3>

<p align="center"><strong>再コンパイル版・改造版（MOD）ではありません!!</strong></p>

**Waddamburoは、独自に新規開発した、まったく新しいエンジンを使用しています。
バンダイナムコのソースコードやコンパイル済みのゲームコードは一切含まれていません。**

WindowsとLinuxで、**AC15時代（2011〜2019年）**のアーケード体験を楽しめます。
FPS制限の解除、グラフィックのアップスケーリング、2人プレイ、
カスタム操作設定、TaikOnlineでのスコア保存とランキングに対応しています。

**正規に入手したオリジナルのゲームデータが必要です。ゲームデータや商用フォントは同梱されていません。**

### インストールと起動

1. [Releases](https://github.com/LucaSilva-r/Waddamburo-public/releases)から、
   Windows x64用の`Waddamburo.exe`、またはLinux x64用の
   `Waddamburo-x86_64.AppImage`をダウンロードします。
2. 正規に入手した[対応バージョン](#compatible-gen3-arcade-versions)のゲームデータフォルダーを開き、
   Waddamburoを**`USRDIR`フォルダー**内の、`data`フォルダーと同じ階層にドラッグして配置します。
3. オリジナルの書体を使う場合は、正規に入手した**DFPKanTeiRyu-XB**を
   **`USRDIR/data/font/font.ttf`**に配置します。`font`フォルダーがなければ作成してください。
   Windowsでのパスは`USRDIR\data\font\font.ttf`です。
4. Waddamburoを起動し、自分のアカウントまたは**Guest**を選んでプレイします。

フォントを追加した後はゲームを再起動してください。
Linuxではファイルの権限設定でAppImageの実行を許可するか、`USRDIR`内で次を実行します。

```sh
chmod +x Waddamburo-x86_64.AppImage
./Waddamburo-x86_64.AppImage
```

**開発と動作確認は現在、主にグリーンVer.を対象にしています。**
他のバージョンでの動作は未確認です。

### TaikOnlineでオンラインプレイ

**アカウントにログインすると、オンラインでスコアを保存し、曲選択でランキングを確認できます。**

1人プレイの曲選択で、曲を選んで**L**を押すと、その曲のランキングを表示できます。

1. プレイヤー選択画面で**Sign in**を選びます。
2. QRコードを読み取るか、ゲームに表示されたコードを[TaikOnline](https://taikonline.com)で入力します。
3. ゲームに戻り、自分のアカウントを選んでプレイしてください。
   次回からも、このPCでアカウントを選べます。

2人プレイでは、それぞれ自分のアカウントを選べます。
友達のPCでは**Join with code**を選ぶと、ログイン情報をそのPCに保存せずに遊べます。
オンライン機能は任意です。ログインせずに遊ぶ場合は**Guest**を選んでください。
オフラインでもプレイできます。

### 基本操作

| 操作 | 1P | 2P |
| --- | --- | --- |
| 左ふち（カッ） | **D** | **Z** |
| 左面（ドン） | **F** | **X** |
| 右面（ドン） | **J** | **C** |
| 右ふち（カッ） | **K** | **V** |

**メニューでは、ふちで移動し、面をたたくと決定します。** 矢印キーとEnterも使えます。
曲選択中に、参加していない側の太鼓をたたくと、もう1人が参加できます。

| キー | 操作 |
| --- | --- |
| **Escape** | ポーズメニューや設定を開く |
| **F11** | フルスクリーンの切り替え |
| **Space** | 起動時の画面をスキップ |
| **L** | 曲選択で選んだ曲のランキングを開く・閉じる（1人プレイ） |
| **Qを長押し** | 演奏中または結果画面から曲をやり直す |

キーの変更やコントローラー・MIDI太鼓の設定は、**Escape → Settings → Drum Controls**から行えます。
メニュー項目名は、画面で見つけやすいよう英語表記で記載しています。

### 設定と追加楽曲

**Escape → Settings**から言語、音量、画質、操作、タイミングを変更できます。

- **追加楽曲：** TJA譜面を`USRDIR/custom_songs`に配置するか、**Settings → Songs**で
  TJA、osu!lazer、ニジイロの楽曲フォルダーを指定してください。

Windows・Linuxでのビルド、テスト、リリース用ファイルの作成、開発・翻訳への参加については、
[ビルド・開発ガイド（英語）](BUILDING.md)を参照してください。

## Legal

Waddamburo is an independent project. It is not affiliated with, authorized by,
sponsored by, or endorsed by Bandai Namco Entertainment Inc. or its affiliates.
"Taiko no Tatsujin" and other referenced names and trademarks belong to their
respective owners and are used only to describe compatibility.

Waddamburo's source code is licensed under the [MIT License](LICENSE).
Third-party components retain their own copyrights and licenses, listed in
[Third-party notices](THIRD_PARTY_NOTICES.md). The MIT license does not replace
those licenses or apply to original game data or commercial fonts.

Original game data and commercial fonts are not distributed with Waddamburo.
You must provide your own legitimately obtained files; their rights remain with
their respective owners.

### 法的情報

Waddamburoは独立したプロジェクトであり、株式会社バンダイナムコエンターテインメントや
その関連会社とは関係がなく、各社の公認・許諾・後援を受けた製品ではありません。
「太鼓の達人」および記載されているその他の名称・商標は、それぞれの権利者に帰属し、
互換性を説明する目的でのみ使用しています。

Waddamburoのソースコードには[MITライセンス](LICENSE)が適用されます。
サードパーティ製コンポーネントの著作権とライセンスはそれぞれ維持され、
[サードパーティの権利表記](THIRD_PARTY_NOTICES.md)に記載されています。
MITライセンスは、これらのライセンスを置き換えるものではなく、
オリジナルのゲームデータや商用フォントにも適用されません。

オリジナルのゲームデータや商用フォントはWaddamburoに同梱・配布されていません。
正規に入手したファイルを各自で用意してください。それらの権利は、それぞれの権利者に帰属します。
