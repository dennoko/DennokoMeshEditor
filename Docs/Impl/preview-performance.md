# NDMF プレビュー軽量化 調査メモ

調査日: 2026-09-25（DenMeshEditor v1.2.5、プロジェクト内の NDMF 1.14.6 を確認）

## 背景

シーン上に DenMeshEditor が付いた Renderer が増えるとシーンが重くなる。
特に、同じメッシュを複数の Renderer が参照しており、それぞれを変形している場合に顕著。

## 結論

コード上の主な負荷候補は、Renderer ごとに 1 つ作られるプレビューノード
（`DenMeshEditorPreviewNode`）の処理。以下のコストが対象 Renderer 数に比例する。

- 全頂点のコピー（`Mesh.GetVertices`）
- メッシュの丸ごと複製（`Object.Instantiate`）
- 全頂点の GPU への再送信（`Mesh.SetVertices`）

`GetVertices` は待機中も定期的に走る。`Object.Instantiate` は生成時・上流メッシュの
差し替え時・ノードの作り直し時に走り、ドラッグの各フレームでは通常は既存メッシュを
`SetVertices` で更新する。同じ Mesh アセットを参照する Renderer 同士でも、現在は
上流頂点と生成メッシュを別々に保持する。

なお、**複数コンポーネントが同じ Renderer を対象にする場合**は別のケース。
`GetTargetGroups` は Renderer ごとに 1 グループを作り、`GatherEdits` がデルタを合算する。
ビルド処理も `HashSet<Renderer>` により各 Renderer を一度だけ処理する。
本メモの重複コストの中心は、**同じ Mesh アセットを複数 Renderer が参照する場合**。

以下は静的調査による改善候補であり、各項目の寄与率は Unity Profiler で未計測。

---

## 優先度：高

### 1. 1 か所の編集で、シーン内の編集済み Renderer がすべて再計算される

`Editor/Preview/DenMeshEditorPreviewNode.cs:121`

```csharp
var rebuild = entry.Version != LiveEdits.Version;
```

`LiveEdits.Version` はシーン全体で 1 つの番号。ドラッグ・確定・Undo のどれで番号が進んでも、
関係のない Renderer まで次の処理をやり直す。

- `GatherEdits`：Dictionary と byte[] を毎回新しく確保する
- `UpdateVertices` 内の `SetVertices`：全頂点を GPU へ送り直す

ドラッグ中はこれが毎フレーム、編集済み Renderer の数だけ走る。
ただし通常の `Rebuild` は既存の `Generated` を再利用し、`Object.Instantiate` はしない。
重い処理が NDMF の `FrameTimeLimiter` の時間枠を使い切ると、残りのノードは打ち切られて
再描画が要求されるため（`ProxyPipeline.OnFrame`）、さらに重く感じられる。

**対策案**

- 変更通知を `MeshEdit` または対象 Renderer ごとに分ける。1 Renderer に複数の
  `MeshEdit` が寄与するため、ノードは自分に関係する編集すべての世代を判定する。
  セッション中の `LiveEdits.Publish`、確定、クリア、Undo/Redo の通知漏れに注意する。
- セッション外の変更は `MeshEdit.Revision` / `Count` / `vertexCount` を対象ごとに監視する。
  `Revision` だけでは、外部からの書き換えや Undo の安全網が弱くなる。
- NDMF の `Refresh` が `this` を返してノードを再利用するときも、対象の編集データが
  変わっていれば次の `OnFrame` で必ず `Rebuild` するよう dirty 状態を伝える。
- ドラッグ中に作り直されるのは編集中の Renderer だけになる。
- 下流フィルタに上書きされる構成の `SyncedVersion` も全体共通なので、同じ対象単位の
  通知へ分けられるか確認する。現在は最短 50 ms 間隔でパイプラインを再構築し、
  下流ノードの `Refresh` 非対応時には重い再生成が起こりうる。

### 2. 待機中も 0.2 秒ごとに全頂点を読み直している

`Editor/Preview/DenMeshEditorPreviewNode.cs:127-132, 156`

上流メッシュがその場で書き換わったかを確かめるため、`GetVertices` で全頂点をコピーしている。
上流フィルタがメッシュを差し替えていない構成（`upstream == 元 Renderer の sharedMesh`）では、
この読み直しを省ける可能性がある。NDMF 自身も元メッシュを監視している
（NDMF `ProxyObjectController.cs:108, 117` の `_monitorMesh.Observe(sharedMesh)`）が、
同じインスタンスの頂点をその場で書き換えた場合の検出は別途検証が必要。

**対策案**

- NDMF の `_monitorMesh.Observe(sharedMesh)` だけでは、その場での頂点書き換えを
  必ず検出できるとは限らないため、読み直しは残す。
- 定期的な軽量プローブと低頻度の全頂点比較を組み合わせる。具体的な検出・更新手順は
  フェーズ 3 に記す。共有キャッシュ（3(a)）でも同じ検出手順を使う。

5 万頂点では頂点位置だけで約 600 KB/Renderer/回。100 Renderer を平均 5 回/秒
読み取ると概算 300 MB/秒になる（ネイティブ処理や他の頂点属性は含まない）。
64 点の `Fingerprint` 比較自体は軽いが、その前の全頂点コピーが支配的。

### 3. 同じメッシュを参照する Renderer ごとに、同じデータを重複して持っている

Renderer ごとに以下を別々に持っている。

| 重複しているもの | 1 つあたりのコスト |
|---|---|
| `Entry.UpstreamVertices` | 頂点数 × 12 バイト。読み直しのたびに全頂点コピー |
| `Entry.Generated`（`Object.Instantiate(Source)`、`DenMeshEditorPreviewNode.cs:233`） | シェイプキー・ボーンウェイト込みの丸ごと複製。シェイプキーの多い素体では数十 MB・数十 ms |
| `MarkDynamic` された頂点バッファ | GPU メモリ |

**対策案**

- **(a) 上流頂点キャッシュの共有**：同一 `Source` インスタンスをキーに、頂点と
  Fingerprint の共有を検討する。同じ Mesh アセットを参照していても、上流フィルタが
  Renderer ごとに別の Mesh インスタンスを出力する場合は共有できない。
  変更検出、キャッシュの寿命、上流のその場での書き換えを扱えることが条件。
- **(b) 生成メッシュの共有**：`(Source, 合成後デルタのハッシュ)` をキーに `Generated` を共有する。
  同じアバターを複数体置いて同じ編集をしている場合、複製と頂点送信が 1 回になる。
  デルタが違う Renderer 同士は共有できないが、(a) の効果は残る。ハッシュ一致だけで
  共有せず、頂点数とデルタ内容も照合する。片方の編集・上流の変更で、他方が参照する
  メッシュをその場で書き換えないようにする。
- 生成メッシュを共有するなら、参照カウントと `Dispose` / ドメインリロード時の破棄、
  `GeneratedMeshTracker`、`DownstreamGuard` の参照同一性による判定を併せて検証する。
  特に `Source` が同じでも上流結果や編集内容が異なれば共有しない。

---

## 優先度：中

### 4. 1 つのコンポーネントが多数の Renderer を対象にすると、監視コストが二乗で増える

`Editor/Preview/DenMeshEditorPreviewFilter.cs:175-181, 226-251`

`EditsFingerprint` はコンポーネントの全 `edits` をハッシュする。そのため：

- 各ノードが同じコンポーネントを監視するので、毎フレームの計算量が「対象 Renderer 数の二乗」になる
  （NDMF の `PropertyMonitor` は抽出関数を毎フレーム再評価する）。
- 1 つの Renderer の編集を変えるだけで、同じコンポーネントに属する全ノードが無効化され、
  全 Renderer 分の `Refresh` が呼ばれうる。`Refresh` がノードを再利用できた場合は
  メッシュの `Object.Instantiate` は起きないが、下流ノードの更新は波及しうる。

**対策案**

- ノード側の `EditsFingerprint` を、その `group.Renderers` に含まれる `edit.target` の
  分だけに絞る。グループ構成の変化は `GetTargetGroups` の `ShapeFingerprint` と
  `WithData` が引き続き検出する必要がある。
- 監視値の変更とともに、`Refresh` で再利用したノードが対象のデルタ変更を反映する
  経路も実装する（上の 1 と「未検証の不具合の可能性」を参照）。

### 5. `Rebuild` のたびにメモリを確保している

`Editor/Preview/DenMeshEditorPreviewFilter.cs:267, 321-322`

プレビュー経路でも `GatherEdits` が毎回 Dictionary と `MeshEdit`（byte[]）を新しく作っている。

**対策案**

- 寄与する編集が 1 件だけで `LiveEdits` の未確定データもないときは、合成せず
  元の `MeshEdit` をそのまま `UpdateVertices` に渡す。
- 複数件のときはノード側で使い回す Dictionary に合成し、Dictionary を受け取る
  `UpdateVertices` を追加する。未確定データがある場合もこの経路を使う。
  ビルド用 `GatherEdits` の合算結果は維持する。

---

## 優先度：低

- **`OnFrame` がカメラの数だけ呼ばれる**：NDMF はカメラごとの OnPreCull から呼ぶため、
  Scene ビューを複数開くと増える。`ProxyRegistry.Report` / `SetSharedMesh` は軽いので現状は問題ない。
- **`DownstreamGuard.OnPostRender` がカメラごとに全件を走査する**：処理自体は軽い。
  1〜3 を直した後に計測して判断すれば十分。

---

## 未検証の不具合の可能性（1 と関連）

編集セッション外で Undo した場合（例：インスペクタのクリアを取り消す）の流れ：

1. `EditsFingerprint` が変わってノードが無効化される。
2. NDMF が `Refresh` を呼び、上流メッシュやプロキシが同じならこちらは `this` を返す
   （`DenMeshEditorPreviewNode.cs:274`）。
3. `LiveEdits.Version` は進んでいないので `Rebuild` が走らない。

このため、プレビューが古いまま残る可能性がある（Unity 上では未確認）。
1 の対策では、`Refresh` 時の dirty 設定とセッション外の対象別フィンガープリントを
必須にして、この可能性も解消する。実際の再現は Unity 上で確認する。

---

## 推奨する着手順

1. **計測と再現確認**：Profiler で待機中の `GetVertices`、ドラッグ中の
   `GatherEdits` / `SetVertices`、生成メッシュ数、NDMF 再構築回数を、Renderer 数を
   増やして比較する。セッション外 Undo の表示も確認する。
2. **1 と 4**：対象別の変更通知・監視と、`Refresh` 再利用時の dirty 伝達を一緒に実装する。
   1 対象だけ編集したときに、無関係なノードの `Rebuild` と下流更新が増えないことを確認する。
3. **2**：上流のその場での変更を取りこぼさない軽量プローブを検証し、全頂点読み直しの頻度を下げる。
4. **5**：再計算時の確保を減らす。ビルド結果とプレビュー結果の一致を確認する。
5. **3(a) → 3(b)**：共有できる上流 Mesh の割合と複製コストを計測して着手する。
   共有メッシュの編集・ノード破棄・ドメインリロードを検証する。

比較する構成は、(a) 別 Renderer が同じ Mesh アセットを参照して同じデルタ、
(b) 同じ Mesh アセットで異なるデルタ、(c) 複数コンポーネントが同じ Renderer を編集、
(d) 上流・下流フィルタの有無、(e) 編集セッション中とセッション外の Undo。

---

# 実装計画（安定性優先）

## 基本方針

軽量化によってプレビューの更新漏れが起きないことを最優先にする。以下を全フェーズ共通の原則とする。

1. **更新の判定は「通知」ではなく「状態の比較」で行う。**
   現在は `LiveEdits.Version`（全体共通の通知番号）が進んだかどうかで再計算を決めている。
   通知の出し忘れはそのまま更新漏れになり、通知を出しすぎると全体の再計算になる。
   各ノードが `OnFrame` で「最後に反映した編集状態」と「現在の編集状態」を比較し、
   違えば作り直す方式（pull 型）に変える。通知は、NDMF にパイプライン再構築を促す
   補助的な用途に限る。
2. **編集内容の再計算判定と、下流への無効化判定は値の完全一致で行う。**
   ハッシュ衝突による更新漏れを構造的になくす。比較対象は編集 1 件あたり数個の値なので、
   完全比較してもコストは小さい。グループ構成用の既存 `ShapeFingerprint` はこのフェーズでは維持する。
3. **迷ったら作り直す側に倒す。**
   判定に失敗した場合や例外が出た場合は dirty のまま残し、次フレームで再試行する。
   「作り直さない」のは、比較で一致を確認できたときだけにする。
4. **共有状態を一時的に書き換える処理は、必ず `try/finally` で元に戻す。**
5. **フェーズごとにコミットを分け、各フェーズの完了時に回帰チェックリストを通す。**
   リスクの高いフェーズ 6 は定数フラグで無効化できるようにしておく。

## 回帰チェックリスト（全フェーズ共通）

プレビューが確実に更新されることを確かめる経路。各フェーズの完了時にすべて確認する。

| # | 操作 | 現在の検出経路 | 新方式での検出経路 |
|---|---|---|---|
| R1 | セッション中のドラッグ | `LiveEdits.Publish` → 全体の `Version` | 対象編集の live スタンプの変化 |
| R2 | ドラッグの確定 | `SetFrom`（Revision++）+ `LiveEdits.Clear` | Revision の変化 + live スタンプが 0 になる |
| R3 | セッション中の Undo / Redo | `ResyncFromComponent` → `ClearLiveEdits` | Revision の巻き戻り（値の不一致） |
| R4 | セッション外の Undo / Redo | `EditsFingerprint` 監視 → `Refresh` が `this` を返す → **Rebuild されない可能性あり** | `Refresh` 後、`NodeController` のコンストラクタ内の `OnFrame` で値の不一致を検出 |
| R5 | インスペクタの「すべてクリア」 | `Clear`（Revision++）+ `LiveEdits.Invalidate` | Revision の変化 |
| R6 | 編集対象の追加・削除・差し替え | `ShapeFingerprint` → グループの再構成 | 変更なし（同じ仕組みを維持） |
| R7 | コンポーネント / GameObject の有効化・無効化・削除 | `GetTargetGroups` の再評価 | 変更なし |
| R8 | 元メッシュの差し替え・FBX の再インポート | NDMF の `_monitorMesh` → パイプライン再構築 | 変更なし |
| R9 | 上流フィルタの出力メッシュの差し替え | `OnFrame` の `upstream != entry.Source` | 変更なし |
| R10 | 上流によるその場での頂点書き換え | 0.2 秒ごとの全頂点読み直し + 64 点比較 | フェーズ 3 で方式を変更（後述） |
| R11 | 下流フィルタに上書きされる構成（AAO など） | `SyncedVersion` 監視 → パイプライン再構築 | 対象単位の比較値に絞って監視 |
| R12 | Prefab の Revert / Apply | `EditsFingerprint` 監視 | R4 と同じ |
| R13 | プレビューの OFF → ON | 全体の `Version` の不一致 | 値の不一致（OFF 中の変更も ON 時に反映） |
| R14 | ドメインリロード（セッション中を含む） | `GeneratedMeshTracker` / `EditSession.End` | 変更なし。共有キャッシュ（フェーズ 5, 6）も破棄されること |
| R15 | Play モードへの出入り、シーンの切り替え | `EditSession.End` | 変更なし |
| R16 | 複数の Scene ビューを開いた状態 | カメラごとの `OnFrame` | 同じフレームで Rebuild が二重に走らないこと |
| R17 | ビルド結果とプレビューの一致 | 共通の `GatherEdits` / `MeshDeltaApplier` | 合成ロジックを 1 か所に保つ（フェーズ 4） |

比較する構成は、上記の (a)〜(e) を使う。

---

## フェーズ 0：計測基盤と再現確認

変更前の基準値を取り、各フェーズの効果と回帰を数値で確認できるようにする。

### 実装内容

- `Unity.Profiling.ProfilerMarker` を追加する（常時残してよいコスト）。
  - `DenMeshEditor.OnFrame`
  - `DenMeshEditor.ProbeUpstream`（`ReadUpstream` + `UpdateFingerprint`）
  - `DenMeshEditor.Rebuild`
  - `DenMeshEditor.GatherEdits`
  - `DenMeshEditor.Instantiate`（`Generated` の生成）
- `DEN_MESH_EDITOR_DEBUG` シンボルが定義されているときだけ有効な内部カウンタを置く
  （Rebuild 回数、全頂点の読み取り回数、生成メッシュ数、ノードの生成 / Refresh 回数）。
  メニューからログに出せるようにする。

### 確認内容

- 構成 (a)〜(e) で、Renderer 数を 1 / 10 / 50 に増やしたときの待機中・ドラッグ中のフレーム時間。
- R4（セッション外の Undo）でプレビューが古いまま残るか。
  再現した場合は、フェーズ 1 の完了条件に「再現しなくなること」を加える。

---

## フェーズ 1：編集状態の比較による再計算判定（問題 1、R4）

### 1-1. `LiveEdits` に編集データ単位のスタンプを追加する

`Editor/Preview/LiveEdits.cs`

- `Dictionary<MeshEdit, int> Stamps` と、単調増加する `_stampCounter` を持つ。
- `Publish(edit, deltas)` では `Stamps[edit] = ++_stampCounter` とする。
- `Clear()` では `Stamps` を空にする（未公開の編集のスタンプは 0）。
- `GetStamp(MeshEdit edit)` は、登録があればその値を、なければ 0 を返す。
- スタンプは単調増加するので、「公開 → クリア → 再公開」でも過去の値と一致しない。
- `Version` / `Invalidate` / `SyncedVersion` は当面残す。`Invalidate` は、
  下流上書き時の同期（`RequestSync`）の起点として使い続ける。ノードは `Version` を参照しなくなる。

### 1-2. 編集状態の比較値（`EditState`）を定義する

`Editor/Preview/DenMeshEditorPreviewNode.cs`（新規の内部型）

1 つの Renderer に寄与する `MeshEdit` 1 件ごとに、次の値を持つ。

| フィールド | 目的 |
|---|---|
| `MeshEdit Edit`（参照） | Undo のデシリアライズでインスタンスが差し替わった場合を検出する。`ReferenceEquals` で比較する |
| `int Revision` | 確定・クリア・Undo による内容の変化 |
| `int Count` | Revision を通らない書き換えに対する安全網（既存の `EditsFingerprint` と同じ考え方） |
| `int VertexCount` | 頂点数の不一致による「適用 / スキップ」の切り替わり |
| `int LiveStamp` | セッション中の未確定データ |

- 収集順は `_components` の順、その中は各コンポーネントの `edits` の順（決定的な順序）。
- 比較は、件数と各要素の完全一致で行う。
- `Entry` に「現在値」と「最後に反映した値」の 2 本の `List<EditState>` を持たせ、
  毎フレーム使い回す（確保しない）。反映に成功したら 2 本を入れ替える。
- 上流側の状態（`Source` インスタンス、上流頂点の世代）も比較に含める。
  フェーズ 3 / 5 で上流の世代番号を入れるまでは、既存の Fingerprint 変化フラグを使う。

コストは、ノードごとに「担当コンポーネントの `edits` 件数」回の比較。
1 コンポーネントが 100 Renderer を対象にしていても、1 フレームあたり約 1 万回の
参照比較と int 比較で済み、マイクロ秒単位に収まる見込み。
`edit.target` との比較は `GatherEdits` と同じ Unity の `!=` を使い、判定を揃える。

### 1-3. `OnFrame` の判定を置き換える

```text
OnFrame(original, proxy):
    ... 既存の Source 差し替え検出 ...
    CollectEditState(entry.Current)
    rebuild = !Equal(entry.Applied, entry.Current) || upstreamChanged
    if rebuild:
        try:
            if Rebuild(entry) == Succeeded:
                swap(entry.Applied, entry.Current)
                commit upstream generation          // 成功したときだけ反映済みにする
        catch (e):
            1 エントリにつき 1 回だけログを出す。Applied は更新しない（次フレームで再試行）
```

- 現行の `Rebuild` は `void` で、`Source` や上流頂点が無い場合に早期 return する。
  成功 / 未実行を返す形へ変更し、`GatherEdits` と `SetVertices` が完了した場合、または
  編集が空で `DestroyGenerated` が完了した場合だけ成功とする。上流をまだ読めない場合は
  未実行として `Applied` と上流世代を更新せず、次のフレームで再試行する。
- `Rebuild` の例外は握りつぶさず、1 回だけログに出す。NDMF の `NodeController.OnFrame` は
  例外を捕まえないため、ここで捕まえないと同じフレームの他ノードの処理
  （`ProxyPipeline.OnFrame` のループ）まで止まってしまう。
- 新規に複製した `Generated` の初回更新が失敗した場合はその複製を破棄する。
  既存メッシュの更新が失敗した場合も反映済み状態は進めず、再試行できるようにする。
- カメラが複数あっても、2 回目以降は比較が一致するので Rebuild は 1 回だけ（R16）。

### 1-4. `Refresh` による再利用と整合させる

- NDMF はノードを再利用する場合も新しい `NodeController` を作り、そのコンストラクタ内で
  `OnFrame()` を呼ぶ（NDMF `NodeController.cs:66`）。そのため `Refresh` が `this` を返しても、
  下流ノードの生成より前に 1-3 の比較が走り、内容が変わっていれば作り直される。
- この性質に依存していることを `Refresh` のコメントに明記する。
- `Refresh` の条件（上流の `Mesh` が変わったら `null`、プロキシの対応が変わったら `null`）は変えない。

### 完了条件

- R1〜R17 がすべて通る。特に R4 / R12 / R13。
- 構成 (b)（同じメッシュで異なるデルタ）で 1 Renderer をドラッグしたとき、
  他の Renderer の `DenMeshEditor.Rebuild` マーカーが 0 回であること。

---

## フェーズ 2：監視範囲の絞り込み（問題 4、下流上書き時の同期）

### 2-1. ノードの編集監視を担当 Renderer の分だけにする

`Editor/Preview/DenMeshEditorPreviewFilter.cs:175-181, 226-251`

- `ObserveNodeInputs(context, components, originals)` で、コンポーネントごとに
  「`edit.target` が `originals` に含まれる編集だけ」の状態を抽出する。
  `originals` はノード生成時に `HashSet<Renderer>` にしてクロージャで捕捉する。
- 抽出値は、対象編集の件数と、各編集の参照・`Revision`・`Count`・`vertexCount` を
  順序どおりに保持した**変更されないスナップショット**にする。
  `ComputeContext.Observe(component, extract, compare)` の比較関数は、両スナップショットを
  要素ごとに完全比較する。ハッシュだけで無効化を決めない。
- NDMF のコンポーネント監視は抽出関数を毎フレーム評価する。スナップショットの確保と
  対象編集の探索コストをフェーズ 0 のマーカーで計測し、高ければ監視の分割方法を見直す。
  対象だけを返すために毎回 `component.edits` 全体を走査する実装では、1 コンポーネントが
  多数の Renderer を対象にする場合の二乗の走査コストは残る。
- セッション外の Undo / Prefab Revert は `SyncedVersion` を進めない場合があるため、
  下流上書き構成でもこのコンポーネント監視が更新通知の必須経路になる。
- `GetTargetGroups` 側の `ShapeFingerprint` / `EditingShapeFingerprint` は変えない（R6）。

### 2-2. `SyncedVersion` の監視を対象単位にする

`Editor/Preview/DenMeshEditorPreviewFilter.cs:119-138`

- 現在は `context.Observe(LiveEdits.SyncedVersion, v => v, ...)` で全体の番号を監視しているため、
  どの対象を編集しても、下流上書きのある全ノードが再構築される。
- NDMF の `Observe(PublishedValue<T>, extract, compare)` を使い、抽出関数では
  「このノードが担当する編集の比較値（1-2 の `EditState` 列）」を返して完全一致で比較する。
  抽出関数は値が変わったときに再評価されるので、関係のないノードは無効化されない。
- この監視はドラッグ中の同期を対象に絞るためのもの。セッション外の Undo / Prefab Revert
  による下流の更新は 2-1 の完全比較で保証し、`SyncedVersion` の発行を前提にしない。
- `SyncedVersion.Value` を更新する時点（`FlushPending`）で、`LiveEdits` のスタンプが
  最新になっていることを確認する（現在の実装で満たされている）。
- 抽出値は毎回新しい配列になるが、評価は `SyncedVersion` の変化時（最短 50 ms 間隔）だけなので許容できる。
- `OverrideGeneration` の監視は変えない（ラッチ成立の検出に必要）。

### 完了条件

- R11 で、ドラッグ中の対象だけが再構築されること。AAO の Remove Mesh 系を下流に置いて確認する。
- 下流フィルタを置いたままセッション外 Undo / Prefab Revert を行い、対象 Renderer の
  下流プレビューが更新され、無関係な対象の `Refresh` が増えないこと。
- 1 コンポーネントで多数の Renderer を対象にする構成で、1 Renderer を確定したときに
  他ノードの `Refresh` が呼ばれないこと（カウンタで確認）。

---

## フェーズ 3：上流読み直しの軽量化（問題 2、R10）

### 前提として確認したこと

- NDMF の引数なし `Observe(obj)` は `ObjectChangeEvents` 経由の変更通知に頼っている
  （NDMF `SingleObjectQueries.cs:125-133`）。スクリプトが `Mesh.SetVertices` などで
  直接書き換えた場合は通知されない。
- そのため `upstream == 元の sharedMesh` の場合でも、読み直しを完全にやめると
  「他のエディタ拡張がアセットメッシュをその場で書き換える」ケースを取りこぼす。
  **読み直しをやめるのではなく、読み直しを安くする方針にする。**

### 3-1. サンプル点だけを読む軽量プローブ

- `Mesh.AcquireReadOnlyMeshData` で位置属性のストリームを直接参照し、64 点だけを読んで
  Fingerprint と比較する。全頂点のコピーは発生しない。
  - `GetVertexAttributeFormat(Position) == Float32` かつ次元が 3 のときだけ使う。
  - ストリーム番号・オフセット・ストライドは `GetVertexAttributeStream` /
    `GetVertexAttributeOffset` / `GetVertexBufferStride` から求める。
  - `MeshDataArray` は `try/finally` で必ず `Dispose` する。
- Fingerprint が変わったときは `GetVertices` で全頂点を一時バッファへ読み、
  3-2 の全件比較で実際の変更を確定する。
- 対応していない形式（Float16 圧縮など）、読み取り不可のメッシュ、例外が出た場合は、
  現在の全頂点読み直しに戻す（安全側）。
- `AcquireReadOnlyMeshData` 自体のコスト（大きなメッシュでコピーが起きないか）は、
  フェーズ 0 の計測で確認してから採用する。

### 3-2. 低頻度の全頂点読み直しを残す

- 現在の方式では、64 点が変わらなくても全頂点は 0.2 秒ごとに最新になるため、
  次の Rebuild では常に新しい上流頂点が使われる。3-1 だけではこの性質が失われる。
- 安全網として、全頂点の読み直しを低頻度（例：2 秒ごと、位相はずらす）で続け、
  内容が変わっていれば上流の世代番号を進めて再計算する。64 点の Fingerprint だけで
  この判定をしない。
- `UpstreamVertices` は前回反映した値として保持し、`GetVertices` は別の使い回し
  `List<Vector3>` に読む。頂点数と**全頂点の x/y/z を厳密に比較**し、1 頂点でも
  異なればリストを入れ替えて `Generation` を進める。差がなければ既存の
  `UpstreamVertices` を維持する。リストを上書きしてから比較しない。
- 全件比較で変更を確定した場合は 64 点の Fingerprint も新しい頂点から更新する。
  初回読み取りや頂点数の変化は必ず変更として扱う。生成メッシュの更新に失敗した場合は
  フェーズ 1 の規則どおり反映済み世代を進めず、次フレームで再試行する。
- 間隔は定数にしておき、計測結果を見て調整する。

### 完了条件

- R8〜R10 が通る。R10 は、テスト用のエディタスクリプトでアセットメッシュの頂点を
  **サンプル点だけ**、**サンプル外の 1 頂点だけ**それぞれ書き換え、前者は次の
  軽量プローブ、後者は低頻度の全件比較で反映されることを確認する。
- 待機中の `DenMeshEditor.ProbeUpstream` では、通常のプローブが 64 点だけを読み、
  全頂点の読み取りと比較が設定した低頻度の間隔でのみ走ることを計測する。

---

## フェーズ 4：Rebuild 時の確保削減（問題 5、R17）

### 4-1. 合成ロジックを 1 か所にまとめる

- `GatherEditsInto(components, target, vertexCount, Dictionary<int, Vector3> destination, List<string> skipped)`
  を追加し、頂点数チェック・未確定データの優先・ゼロデルタの除外の規則をここに集める。
- ビルド用の `GatherEdits` はこれを呼んでから `SetFrom` する形にし、結果が変わらないようにする。

### 4-2. プレビュー用の経路

- ノードは、合成用の `Dictionary<int, Vector3>` と、復元用の `List<int>` / `List<Vector3>` を使い回す。
- `MeshDeltaApplier.UpdateVertices` に Dictionary を受け取るオーバーロードを追加し、
  書き込んだインデックスを記録して復元する。
- 寄与する編集が 1 件で未確定データもない場合は、既存の `MeshEdit` 版を直接呼ぶ。
- 「書き込み → `SetVertices` → 復元」は `try/finally` で囲み、例外が出ても基準頂点を必ず戻す。
  フェーズ 5 で上流頂点を共有すると、戻し忘れが他の Renderer に波及するため。

### 完了条件

- R17：上流フィルタのない構成で、プレビューの `Generated` の頂点と
  `MeshDeltaApplier.CreateEdited` の結果が一致すること（デバッグ用メニューで比較する）。
- ドラッグ中に `GatherEdits` による GC 確保が発生しないこと。

---

## フェーズ 5：上流頂点の共有（問題 3(a)）

### 実装内容

新規の `UpstreamVertexCache`（static クラス）を作る。

- キー：上流の `Mesh` インスタンス（参照で比較）。
- 値：`Vertices`（List）、`Fingerprint`、`Generation`（内容が変わるたびに増える）、
  `NextProbe` / `NextFullRead`、`RefCount`、`WarnedNotReadable`。
- `Acquire(mesh)` / `Release(mesh)` は、`Entry.Source` の設定・変更・`Dispose` 時に対にして呼ぶ。
  参照カウントが 0 になったら削除する。NDMF の再構築では新ノードの生成後に旧ノードの
  `Dispose` が走ることがあるが、参照カウントなので問題ない。
- プローブはキャッシュ単位で行う。同じ Mesh を参照する Renderer がいくつあっても 1 回で済む。
- ノードは 1-2 の比較値に `Generation` を含め、上流が変わったら作り直す。
- `Release` には保持している参照そのものをキーとして使う（破棄済みの Mesh でも、
  Unity の null 判定に左右されないようにする）。
- 中身は static なマネージドデータだけなので、ドメインリロードで自然に消える（R14）。

### 注意点

- `UpdateVertices` は基準頂点を一時的に書き換えて元に戻す実装。共有リストでも処理は
  同期的に完了するので安全だが、フェーズ 4 の `try/finally` が前提になる。
- 上流フィルタが Renderer ごとに別の Mesh インスタンスを出力する場合は共有されない（現状と同じ）。

### 完了条件

- 構成 (a)(b) で、全頂点の読み取り回数が「ユニークな上流 Mesh の数」に比例すること。
- R8〜R10、R14 が通る。

---

## フェーズ 6：生成メッシュの共有（問題 3(b)、既定では無効）

効果があるのは「同じ Mesh・同じデルタ」の構成 (a) だけで、寿命管理のリスクが最も高い。
フェーズ 0〜5 の計測で、`Instantiate` とメッシュのメモリが依然として支配的な場合にだけ着手する。
定数フラグで無効化できるようにし、既定では無効にする。

### 実装内容

新規の `SharedGeneratedMeshCache` を作る。

- キー：`(上流 Mesh インスタンス, 上流の Generation, 合成後デルタの内容)`。
  ハッシュはバケット分けにだけ使い、ヒットしたらインデックス列とデルタ列を完全比較する。
- 値：生成メッシュ、デルタ内容のコピー、`RefCount`。
- **コピーオンライト**：参照カウントが 2 以上のメッシュには書き込まない。
  内容が変わったエントリは、今のメッシュを `Release` してから新しいキーで `Acquire` し直す。
- **編集セッションの対象は共有しない**：`EditSession.ActiveComponent` の対象 Renderer は
  常に専用のメッシュを持つ。ドラッグのたびに複製が走るのを避けるため。
  セッション開始時に 1 回だけ複製が走るのは許容する。
- 破棄は参照カウントが 0 になったときだけ行う。`GeneratedMeshTracker` には共有メッシュを
  1 回だけ登録し、ドメインリロード時に破棄されることを確認する。
- `DownstreamGuard` はプロキシ単位で「代入したメッシュの参照」を比較するので、
  共有しても判定は変わらない。

### 完了条件

- 構成 (a) で、生成メッシュの数が 1 になること。
- 共有中の 1 つを編集したとき、他の Renderer の表示が変わらないこと。
- ノードの破棄順を変えても（コンポーネントの削除・無効化、プレビュー OFF）、
  破棄済みのメッシュを参照するプロキシが出ないこと。
- R1〜R17 がすべて通る。

---

## 着手順とリリース単位

| 順 | フェーズ | 効果 | リスク | リリース |
|---|---|---|---|---|
| 1 | 0 計測基盤 | なし（計測のみ） | 低 | 単独でコミット |
| 2 | 1 状態比較 | ドラッグ中の全体再計算の解消、R4 の修正 | 中 | フェーズ 1 と 2 をまとめて 1 リリース |
| 3 | 2 監視の絞り込み | 多対象コンポーネント・下流上書き構成の改善 | 中 | 同上 |
| 4 | 4 確保削減 | GC の削減 | 低 | 上と同時でも可 |
| 5 | 3 軽量プローブ | 待機中の負荷の削減 | 中 | 単独リリース |
| 6 | 5 上流頂点の共有 | 同じメッシュを参照する構成の改善 | 中 | 単独リリース |
| 7 | 6 生成メッシュの共有 | 同じメッシュ・同じデルタの構成の改善 | 高 | 計測次第。既定では無効 |

フェーズ 4 はフェーズ 5 の前提（`try/finally` による復元）を含むため、フェーズ 5 より先に行う。
