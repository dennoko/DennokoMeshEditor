# DenMeshEditor 概要

VRChat アバター改変向けの、**非破壊メッシュ編集ツール**。

Unity エディタ上で頂点を直接動かして体型・服の食い込みなどを調整し、その結果を NDMF のビルドパイプラインで非破壊的に反映する。

---

## 1. コンセプト

| 原則 | 意図 |
| --- | --- |
| 簡単 | 頂点を掴んで動かす、それ以上の学習コストを要求しない |
| シンプル | 機能を絞る。設定項目を増やさない |
| 軽量 | シーンロード・ドメインリロードを重くしない（重い dll 依存を持たない） |
| 非破壊 | 元メッシュアセットを一切書き換えない。編集内容はコンポーネントに保持 |
| 効率化 | 「開く → 動かす → 閉じる」で完結。保存操作を挟ませない |

### 既存ツールとの差別化

EreMorph などの高性能ツールが既に存在するが、

- 高機能ゆえに目的の操作までの手数が多い
- 大規模シーンのロード時、dll のロードが遅い

という点が実運用で気になる。DenMeshEditor は**機能を絞ることで手軽さと軽さを取る**方針を採る。EreMorph の上位互換を目指すものではなく、「日常的なちょっとした調整」を最速で終わらせるためのツールと位置づける。

---

## 2. 対象環境

- **Unity 2022.3.22f1 以降**（最新の Unity 6 まで対応）
- VRChat SDK (com.vrchat.avatars)
- **NDMF (nadena.dev.ndmf) 必須** — 開発時の検証バージョンは 1.11.0
- Modular Avatar との併用を前提（MA Scale Adjuster などの影響下で編集できること）

---

## 3. 用語

| 用語 | 意味 |
| --- | --- |
| 元メッシュ | `SkinnedMeshRenderer.sharedMesh` / `MeshFilter.sharedMesh` に設定された、プロジェクト内のアセット |
| プロキシ | NDMF プレビューが生成する、他ツールの改変が適用済みの一時 Renderer |
| デルタ | 頂点ごとの移動量（`Vector3`）。編集結果の実体 |
| ベースメッシュ | プロキシから読んだ、デルタを載せる前の形状 |
| 編集操作 | 1 回のドラッグ。(中心座標 C, 半径 r, 減衰 f, 変位 D) で表現される |
| ベイク | 編集結果を適用した Mesh アセットをディスクに書き出す操作 |

---

## 4. スコープ

### やること

- 頂点選択とプロポーショナル編集
- 任意の軸でのミラー編集
- NDMF プレビューへの編集結果の反映
- NDMF によるビルド時の非破壊適用
- ベイク（編集後メッシュのアセット書き出し）
- 複数メッシュの同時編集

### やらないこと（意図的な非対応）

- トポロジ編集（頂点・辺・面の追加／削除、押し出し、細分化）
- UV / 法線 / ウェイトの手編集
- マテリアル・サブメッシュの編集
- アニメーション、シェイプキーの手作成（ベイク時のシェイプキー化は除く）
- 測地距離ベースのプロポーショナル編集（軽量性を優先しユークリッド距離のみ）

トポロジを変えないことは仕様上の重要な前提であり、後述するデータモデル（頂点インデックス基準のデルタ）の成立条件でもある。

---

## 5. 機能要件

### 5.1 頂点選択とプロポーショナル編集

- シーンビュー上で頂点を選択（クリック選択、矩形選択）
- 選択頂点をハンドル（移動 Gizmo）で操作
- プロポーショナル編集
  - 影響半径をユーザーが指定
  - **距離基準はユークリッド距離**（トポロジ探索を行わない。軽量性を優先）
  - 減衰カーブを選択可能（スムーズ / 直線 / シャープ など少数のプリセット）
  - 半径はシーンビュー上で可視化する

### 5.2 ミラー編集 — 「仮想中心」方式

**対称頂点のペア探索は行わない。**編集操作そのものをミラーする。

1 回の編集操作を **(中心座標 C, 半径 r, 減衰 f, 変位 D)** として表現し、ミラー ON のときは同じ操作を反対側にも適用する。

```
ミラー軸を X とし、反射行列を R = diag(-1, 1, 1) とする

通常側:   各頂点 v について  w  = f(|v - C |/r)   → v += D  · w
ミラー側: C' = R·C,  D' = R·D
          各頂点 v について  w' = f(|v - C'|/r)   → v += D' · w'
```

**要点：変位ベクトル D も反射する。**中心 C だけを反転して D をそのまま使うと、+X に引いたとき反対側も +X に動いてしまう。R を C と D の両方に掛けることで、「外側に膨らませる」操作が両側で「外側」になる。

#### この方式の利点

- **対称トポロジを要求しない。**左右で頂点数・頂点配置が違うメッシュでも動く
- 対称判定の許容誤差というパラメータが不要（設定項目が増えない＝コンセプトに合致）
- 非対称モデル、片側だけにパーツがあるモデルでも破綻しない
- 実装が単純。通常側の処理を、C と D を差し替えてもう一度呼ぶだけ

**実装可能性：問題なし。**むしろ頂点ペア方式より実装量・計算量ともに小さい。

#### ミラー基準空間

複数メッシュを同時編集するため、**共通の基準空間が必要**。アバタールート（NDMF の Avatar Root）のローカル空間を基準とし、ミラー面はそこでの `x = 0` 平面とする。アバタールートが取れない場合はコンポーネントの Transform にフォールバックする。

#### 既知の挙動：中心線付近

C がミラー面のごく近くにあると、2 つの影響球がほぼ重なる。中心線上の頂点では軸方向成分は打ち消し合う（正しい）が、軸に垂直な成分は約 2 倍になる。

対処として、**`|C.x| < ε` のときはミラー側の適用をスキップする**（通常の編集として扱う）。中心線上の操作は本質的に左右対称なので、二重適用を避けるこの挙動が最も予測しやすい。ε の既定値は要調整。

### 5.3 複数メッシュの同時編集

- 1 つの編集セッションで複数の Renderer をまとめて扱える
- メッシュ境界をまたいだプロポーショナル編集ができること（服と素体を同時に馴染ませるため）
- 中心 C と半径 r はアバタールート空間で定義されるため、複数メッシュへの適用は自然に成立する

### 5.4 NDMF 連携

- 編集中：NDMF プレビューに編集結果をリアルタイム反映
- ビルド時：NDMF のビルドパイプラインで非破壊的にメッシュを差し替え

### 5.5 ベイク

- コンポーネント上のベイクボタンから実行
- チェックボックスで **「シェイプキーとして追加するか」** を選択可能
  - OFF：頂点座標を直接書き換えたメッシュを出力
  - ON：元の形状を保ったまま、編集分を新規シェイプキーとして追加したメッシュを出力
- 保存先：**元メッシュのあるフォルダと同一**
- 命名：元メッシュ名に `_edited` をサフィックス付与
  - 元メッシュ名に既に `_edited` が付いている場合は追加しない
- 名前衝突時：末尾に **スペース + 重複しない番号** をインクリメントして付与
  - 例：`Body_edited` → `Body_edited 1` → `Body_edited 2`
- **アセット出力のみを行う。**シーン内 Renderer の自動差し替えはしない

---

## 6. 操作フロー

```
1. 対象メッシュ（あるいは任意のオブジェクト）にコンポーネントをアタッチ
2. コンポーネントの「編集」ボタンを押す
      ├─ シーンビューが編集モードに入る
      ├─ 頂点を選択して動かす
      └─ ミラーボタン ON の間の操作のみミラー処理される
3. 「編集終了」で編集モードを抜ける
4. そのままアップロード → ビルド時に編集結果が反映される
```

保存ボタンは設けない。編集結果はコンポーネントのシリアライズ対象として保持され、Undo / Prefab / シーン保存の通常フローに乗る。

### 6.1 ハンドル操作中のブラシ半径変更

ハンドルを掴んだまま **マウスホイールで影響半径を変更**でき、結果はその場でプレビューに反映される。Blender のプロポーショナル編集と同じ操作感で、「どこまで馴染ませるか」を変位を与えた状態のまま決められる。

- 変化は乗算（1 目盛りあたり一定倍率）。半径が小さいときは細かく、大きいときは粗く動く
- 影響範囲の再計算に使う頂点位置はドラッグ中は更新されない＝ドラッグ開始時の形状のままなので、選択時とまったく同じ基準で計算し直せる
- 半径を縮めた場合、範囲から外れた頂点はスナップショットから復元されて元の位置に戻る（`ApplyDisplacement` が毎回スナップショットから作り直す設計がそのまま効く）
- 変更した半径はマウスを離した時点でコンポーネントへ確定する。デルタと同じ Undo エントリに入る
- **横取りするのはハンドルを掴んでいる間だけ。**それ以外ではシーンビューのズームを妨げない

条件は「掴んでいるか」（`_handleGrabbed`）であって「動かしたか」（`_dragging`）ではない。**掴んでから動かす前に半径を決める**という手順が自然で、実際そこでホイールがシーンビューのズームになると直観に反する。`_dragging` はハンドルが実際に値を返したときにしか立たないので、この 2 つは別に持つ必要がある。

掴んだ判定は `Handles.PositionHandle` の呼び出し前後で `GUIUtility.hotControl` を比べて取る（前が 0 で後が非 0 なら、掴まれたのは自分のハンドル）。この場でハンドルが取った ID そのものを見るので、シーンビューの他のコントロールと取り違えない。

なお「掴んだが動かさなかった」場合、確定するデルタは無いが**半径だけは書き込む必要がある**。リリース時の早期リターンは `_dragging` だけでなく `_hasPendingRadius` も見る（見落とすと、変更した半径が未確定のまま次のドラッグまで宙に浮く）。

### 6.2 頂点のピッキング：見えているものだけを対象にする

単純な「スクリーン座標で最も近い頂点」では、**裏側の頂点を掴んでしまう**。胴体や腕のように閉じた形状では、手前の面と奥の面がスクリーン上でほぼ重なるため頻繁に起きる。

そこで 3 段階で選ぶ。

```
1. 全頂点のスクリーン座標と、視線方向の深度を求める
2. クリック位置から一定距離内の頂点を、近い順に最大 8 件まで候補として集める
3. 近い順に遮蔽判定を行い、最初に「遮蔽されていない」ものを選ぶ
```

遮蔽判定はカメラから頂点へのレイと、編集対象メッシュの三角形との交差判定（Möller–Trumbore）で行う。**法線による裏面カリングは使わない** — 片面モデリングされたスカートや髪カードを裏側から編集できなくなるため。レイ交差なら両面表示のマテリアルでも正しく扱える。

**コストを候補数に依存させない工夫：** 候補ごとに全三角形を走査すると候補数に比例して重くなる。候補はすべてクリック位置から 24px 以内にあるので、**三角形のスクリーン外接矩形を 24px 広げてクリック位置を含むものだけ**をピック 1 回につき一度だけ集めておけば、どの候補に対しても取りこぼしがない。実際に交差判定を行う三角形は数個〜数十個に落ちる。

さらに候補ごとの絞り込みとして、**3 頂点とも対象頂点より奥にある三角形は飛ばす**。ここで使う深度はカメラからの直線距離ではなく**視線方向への射影**にする。射影は位置に対して線形なので、3 頂点が奥にあれば面全体が奥にあると言い切れる（直線距離ではこれが成り立たない）。

**自己交差の回避：** 頂点は自分が属する三角形の上に乗っているため、レイは必ずその面と交差する。距離に比例した余裕（`max(1e-4, distance * 0.003)`）を引いた位置より手前の交差だけを遮蔽として扱う。

**制限：** 遮蔽物として見るのは**編集対象として登録されている Renderer だけ**。たとえば素体だけを登録して服を登録していない場合、服に隠れた素体の頂点も選択できてしまう。両方を登録すれば解決する。

なお、ここでのレイ交差による遮蔽判定を行うのは**選択時のみ**。プロポーショナル編集の影響範囲はユークリッド距離で決まるため、薄い面の裏側の頂点も従来どおり一緒に動く（これは意図した挙動）。表示側の遮蔽については 6.3 を参照。

### 6.3 影響範囲の表示：隠れている頂点は描かない

影響範囲のプレビュードットを素通しで描くと、**胴体や腕の裏側の頂点まで一緒に見えて画面がごちゃつく**。ブラシ半径が球であるため、閉じた形状ではほぼ必ず裏面の頂点が範囲に入る。

ここでは 6.2 のレイ交差は使わず、**シーンビューの深度バッファとの比較**（`Handles.zTest = LessEqual`）で落とす。

- **CPU コストがゼロ。**表示は毎フレーム・最大 3000 点に対して走るので、点ごとにレイ交差をやり直す余裕はない
- **遮蔽物を選ばない。**編集対象として登録していない服や小物に隠れた頂点も正しく消える（レイ交差版の「遮蔽物は登録済み Renderer だけ」という制限が無い）
- **法線を見ない。**6.2 と同じ理由で裏面カリングは使わない。トゥーンシェーダ向けに法線を編集したアバターでも誤判定しない

2 点だけ補正が要る。

- **面の上に浮かせる** — 頂点は面のちょうど上に乗っているため、そのまま描くと深度比較が拮抗してドットが明滅する。カメラ側へ「ドット半径の 2 倍」だけ浮かせる。見かけの大きさに比例する量なので、スクリーン上の浮き量は距離やズームによらず一定になり、メッシュの厚みを越えて裏の頂点まで見えてしまうこともない
- **ドラッグ中はドットも変位させる** — ドラッグ中 `WorldVertices` は開始時のまま更新されない（→ 6.1）一方、プレビューメッシュは既に変形している。ドットを元の位置に描くと、動かした先の面に埋もれて全部消えてしまう。描画位置に `displacement × weight`（ミラー分を含む）を足して、変形後のメッシュと同じ位置に置く

深度バッファに書かない材質（`ZWrite Off` の半透明シェーダなど）では遮蔽が効かず、従来どおり全部描かれる。悪化はしない。

ブラシ円も同じ変位で動かす（ミラー円はミラー側の変位）。中心の頂点は重み 1 でハンドルの変位をそのまま受け取るので、動かした先でも「円の中が影響範囲」という対応が保たれる。円だけ選択時の位置に取り残されると、ドット群と分離して見えて位置を見失う。なお円自体は深度比較の対象にしない（面に隠れると位置が分からなくなるため）。

---

## 7. コンポーネント仕様

### 7.1 対象 Renderer の指定

```csharp
public class DenMeshEditor : MonoBehaviour
{
    public List<Renderer> targets;   // 複数指定可
    public List<MeshEdit> edits;     // targets と対応する編集データ
}
```

- **対象 Renderer を複数指定できる**
- Renderer を持つ GameObject にアタッチされた場合、**その Renderer が自動で `targets[0]` にセットされる**
  - Unity の `Reset()` コールバックで `GetComponent<Renderer>()` を試み、取得できたら登録する
  - `Reset()` はコンポーネント追加時に呼ばれるため、追加直後から使える状態になる
  - 既に `targets` に要素がある場合は上書きしない
- アバタールートなど Renderer を持たないオブジェクトにアタッチした場合は、手動で追加する

### 7.2 アセンブリ構成

姉妹ツール（NdmfObjectActivater 等）の規約に揃える。

```
Assets/dennokoworks/DennokoMeshEditor/
├── Runtime/
│   ├── dennokoworks.DenMeshEditor.Runtime.asmdef
│   └── DenMeshEditor.cs                  … MonoBehaviour（データ保持のみ）
├── Editor/
│   ├── dennokoworks.DenMeshEditor.Editor.asmdef
│   ├── DenMeshEditorInspector.cs         … Inspector UI
│   ├── DenMeshEditorSceneTool.cs         … シーンビューでの編集
│   ├── DenMeshEditorPlugin.cs            … NDMF ビルドプラグイン
│   ├── DenMeshEditorPreviewFilter.cs     … IRenderFilter / IRenderFilterNode 実装
│   ├── ProxyRegistry.cs                  … プロキシ Renderer の受け渡し
│   ├── MeshDeltaApplier.cs               … デルタ適用の共通ロジック
│   └── DenMeshEditorBaker.cs             … ベイク処理
└── Docs/
```

- 名前空間：`Dennokoworks.DenMeshEditor` / `Dennokoworks.DenMeshEditor.Editor`
- NDMF プラグイン `QualifiedName`：`dennokoworks.den-mesh-editor`
- `DisplayName`：`Dennoko Mesh Editor`

**Runtime アセンブリはデータ保持に徹する。**編集ロジック・プレビュー・ベイクはすべて Editor 側に置く。これはビルド成果物を軽くするためであり、「軽量」というコンセプトの実装上の担保でもある。

プレビュー適用とビルド適用は**同一の `MeshDeltaApplier` を共有する**。プレビューとビルド結果が食い違わないことを、コードの共有によって保証する。

---

## 8. データモデル — 頂点ごとのデルタ

### 8.1 結論

**頂点ごとにデルタを持ち、加算のみで反映する方式で問題ない。**むしろこれが、上流ツールがどんなメッシュ変形をしていても動作させるための唯一の堅実な方法。

```csharp
[Serializable]
public class MeshEdit
{
    public Renderer target;
    public int      vertexCount;  // 整合性チェック用

    [SerializeField] private int    count;     // 編集された頂点数
    [SerializeField] private byte[] blob;      // 16 バイト/頂点：int32 index + float32 x,y,z
    [SerializeField] private int    revision;  // 書き込みのたびに ++（変更検出用）
}
```

### 8.1.1 なぜ byte[] 1 本なのか — 大量配置時のロードコスト

論理的なデータは「インデックス + デルタ」のままだが、**保持形式は `byte[]` 1 本**にする。`List<int>` / `List<Vector3>` を素直に持つと、シーン上にコンポーネントを大量に置いたときに次の 2 つが線形に効いてくる。

**シーン／Prefab のロード時間**

Force Text の YAML では `List<Vector3>` が 1 頂点あたり 4 行に展開される。1 万頂点で 4 万行。対して `byte[]` は Unity のバイナリブロブとして **1 行の 16 進文字列**になる（同プロジェクト内の `Mesh._typelessdata` や `m_LayerCollisionMatrix` と同じ表現）。行数もパースコストも桁違いに小さい。

**Prefab オーバーライドの件数**

`PrefabUtility.RecordPrefabInstancePropertyModifications` は配列を `Array.data[i]` 単位で記録する。1 万頂点なら `indices` と `deltas` で **2 万件の PropertyModification** が Prefab インスタンスに積まれ、Prefab を含むシーンのロードとインスペクタ操作が極端に重くなる。`byte[]` ならプロパティ 1 件で済む。

**変更検出（`revision`）**

NDMF の `ComputeContext.Observe` は監視値の抽出関数を**毎フレーム**再評価する（`PropertyMonitor.CheckAllObjectsLoop`）。デルタ全体をハッシュしていると「コンポーネント数 × 編集頂点数」のコストが常時かかるため、書き込みのたびに増える `revision` を見るだけで済ませる。

`revision` は**シリアライズ対象**であることが重要で、Undo / Redo・Prefab の Revert・ドメインリロードのいずれでもデータと一緒に巻き戻る。static なカウンタでは、これらの経路で状態が食い違う。

**旧形式（v1）からの移行**

`indices` / `deltas` のフィールドは残し、初回アクセス時に `blob` へ移行する。既存シーンのデシリアライズを壊さないため。移行判定は「両方 null なら即 return」の O(1) 経路であり、毎フレーム走るフィンガープリント計算から呼ばれても問題にならない。

### 8.2 なぜ任意の変形に耐えるのか

適用処理は次の 1 行に集約される。

```csharp
outVerts[i] = baseVerts[i] + delta[i];
```

`baseVerts` が何であるかを**一切問わない**。上流ツールがスケールを変えていようが、頂点を大きく動かしていようが、シェイプキーを焼き込んでいようが、我々はその結果に対して加算するだけ。これは**シェイプキーとまったく同じ意味論**であり、シェイプキーが任意のベースメッシュに対して機能するのと同じ理由で機能する。

「上流が何をしたかを推論して補正する」設計にしないことが要点。推論を一切しないので、推論が外れることもない。

### 8.3 唯一の前提条件：頂点数と頂点順序の保存

この方式が要求するのは**頂点インデックスの同一性だけ**。上流ツールが頂点を追加・削除・並べ替えするとデルタの対応が壊れる。

対策：

- **ビルドフェーズを `Transforming` にする。**頂点数を変える代表格である Avatar Optimizer（メッシュ統合、ポリゴン削除）は `Optimizing` フェーズで動くため、`Transforming` で処理すれば必ず先行できる
- `vertexCount` を記録しておき、適用時に実際の頂点数と照合する。不一致なら適用をスキップして警告を出す
- 元メッシュの再インポートや FBX 更新で頂点順が変わった場合も、この照合で検知できる（頂点数が同じまま順序だけ変わるケースは検知できないが、実運用では稀）

### 8.4 デルタの保持空間

デルタは**メッシュローカル（バインドポーズ）空間**で保持する。これは `Mesh.vertices` 配列と同じ空間なので、適用時に変換が不要になる。

シーンビューでのドラッグはワールド空間で発生するため、**保存時にスキニング行列の逆行列で変換する**（詳細は 9.3）。

副次的な性質として、後から Scale Adjuster の値を変えると、メッシュローカルのデルタは骨のスケールに追従して拡縮する。「体型に対する相対的な調整」として保存されることになり、直感に合う挙動と判断する。

---

## 9. NDMF 連携

### 9.1 リフレクションは使わない — プッシュ型で受け取る

参考記事：https://zenn.dev/dennoko/articles/978b1b2617e33d

記事では `PreviewSession.OriginalToProxyRenderer`（`internal`）へのリフレクションでプロキシを取得している。これは **シーンビュー側から NDMF に問い合わせる（プル型）** アプローチであり、その用途の公開 API が無いために必要となっていた。

しかし **`IRenderFilterNode.OnFrame` を使えば、公開 API だけでプロキシを受け取れる。**

```csharp
public interface IRenderFilterNode : IDisposable
{
    /// Invoked on each frame ...
    /// This function is passed the original and replacement renderers
    public void OnFrame(Renderer original, Renderer proxy) { }
}
```

NDMF 1.11.0 における呼び出し経路（`Editor/PreviewSystem/Rendering/`）：

```
ProxySession.OnPreCull(isSceneCamera)                    … ProxySession.cs:146
   └─ ProxyPipeline.OnFrame(isSceneView)                 … ProxyPipeline.cs:385
        └─ NodeController.OnFrame()                      … NodeController.cs:67
             └─ _node.OnFrame(original, proxy.Renderer)  … NodeController.cs:80
```

つまり **自前のフィルタノードの `OnFrame` が、毎フレーム、プロキシ Renderer を引数で受け取る。**

さらに重要な点として、この呼び出し元は **`OnPreCull`** である。記事が挙げていた「プロキシはフレームごとに更新されるため `OnPreCull` 以降で読む必要がある」というタイミング制約が、**この経路では構造的に満たされる。**自分でタイミングを合わせる必要がない。

**方針：プル型をやめてプッシュ型にする。シーンビューが NDMF に問い合わせるのではなく、フィルタが受け取ったプロキシをシーンビューへ渡す。リフレクションは一切使用しない。**

### 9.2 プロキシの受け渡し

```csharp
// Editor 内部の静的レジストリ
internal static class ProxyRegistry
{
    static readonly Dictionary<Renderer, Renderer> _map = new();

    internal static void Report(Renderer original, Renderer proxy) => _map[original] = proxy;

    // 再構築では「新ノードの登録 → 旧ノードの Dispose」の順になることがあるので、
    // 自分が登録したプロキシと一致する場合だけ消す
    internal static void Remove(Renderer original, Renderer proxy) { ... }

    internal static bool TryGet(Renderer original, out Renderer proxy)
        => _map.TryGetValue(original, out proxy) && proxy != null;  // Unity の null チェック必須
}

// フィルタノード側
public void OnFrame(Renderer original, Renderer proxy)
{
    ProxyRegistry.Report(original, proxy);
    // …併せて編集済みメッシュの差し込みもここで行う（→ 9.2.1）
}
```

シーンビューの編集ツールは `ProxyRegistry.TryGet` でプロキシを引き、そこから頂点位置を得る。

**寿命の注意点：** プロキシはパイプライン再構築のたびに破棄・再生成される。レジストリの参照は必ず Unity の null 比較（`proxy != null`）で検証してから使う。`IRenderFilterNode.Dispose` での登録解除と合わせて二重に守る。

**プレビュー無効／フィルタ未起動の場合：** `TryGet` が false を返す。この場合は元 Renderer の `sharedMesh` にフォールバックし、「他ツールの影響が反映されていません」と Inspector に警告表示する。編集自体は可能な状態を保つ。

### 9.2.1 編集済みメッシュの差し込みは毎フレーム行う

**`Instantiate` でプロキシに `sharedMesh` を設定しても表示には反映されない。**

`ProxyPipeline.OnFrame` はフレームごとに次の順で回る（`ProxyPipeline.cs:363-397`）。

```
1. すべての proxy に対して ProxyObjectController.OnPreFrame()
2. すべての node に対して NodeController.OnFrame() → IRenderFilterNode.OnFrame()
3. すべての proxy に対して ProxyObjectController.FinishPreFrame()
```

問題は 1 で、`ProxyObjectController.OnPreFrame` は無条件に

```csharp
replacementSMR.sharedMesh = smr_.sharedMesh;   // ProxyObjectController.cs:189
replacementSMR.bones      = smr_.bones;
```

を実行し、プロキシのメッシュを**元 Renderer のものへ戻す**。`Instantiate` 時の代入は描画される前に必ず上書きされる。

したがって **メッシュの生成は `Instantiate`（または初回の `OnFrame`）で 1 度だけ行い、プロキシへの代入は毎フレーム `OnFrame` で行う。** これは NDMF の他ツールでも同じで、Avatar Optimizer（`AAORenderFilterBase.cs:134`）も Modular Avatar（`RemoveVertexColorPreview.cs:105`）も `OnFrame` で `sharedMesh` を代入し直している。

`IRenderFilterNode.OnFrame` の XML ドキュメントには「generally, you should not modify the mesh or materials in this method」とあるが、これは**内容の作り直し**を毎フレームやるなという意味であり、既に生成済みのメッシュを差し込む代入は必要な処理である。

**上流メッシュの再取得：** 自分の `OnFrame` が呼ばれる時点でプロキシの `sharedMesh` は「1 でリセットされ、2 の上流ノードが適用済み」の状態、つまり**上流の出力そのもの**になっている。これをデルタ加算の基準（ベース）として保持し、インスタンスが変わったときだけ取り直す。

#### 自分の編集分の二重適用について

`OnFrame` で受け取るプロキシの `sharedMesh` は上流の出力であり、**自分のデルタは載っていない**（毎フレームリセットされるため）。よってベースにデルタを加算する処理は常に 1 回だけ効き、二重適用は起きない。

一方、シーンビュー編集ツールが `ProxyRegistry` 経由で読む形状は、自分の `OnFrame` が代入した後の状態、つまり **ベース + 自分のデルタ** になる。これは表示・ピッキングにとって正しい（現在の編集結果そのもの）。新しいドラッグは既存デルタへの増分として加算される。

#### 補足：MA Scale Adjuster の挙動

`nadena.dev.modular-avatar/Editor/ScaleAdjuster/ScaleAdjusterPreview.cs` を確認したところ、Scale Adjuster は `WhatChanged => RenderAspects.Shapes` を返し、`OnFrame` で `smr.bones` をスケール調整済みのシャドウボーンへ差し替える実装になっている。`sharedMesh` 自体は書き換えていない。

つまり **Scale Adjuster の効果はプロキシ Renderer の `bones` 側に載っている。**だからこそ「プロキシ *Renderer* を掴む」ことが本質であり、`sharedMesh` を単体で取り出すのではなく、Renderer 経由でスキニング結果を得る必要がある（→ 9.3）。

### 9.3 編集セッションでの座標変換

上流ツールは 2 種類に分かれ、プロキシ Renderer 経由で読むことで両方がまとめて反映される。

| 上流ツールの種類 | 効果の現れる場所 | 反映のされ方 |
| --- | --- | --- |
| メッシュを変形するもの | プロキシの `sharedMesh` の頂点配列 | プロキシの `sharedMesh` に載る |
| ボーンを操作するもの（Scale Adjuster 等） | プロキシの `bones` / Transform | スキニング結果に載る |

**表示・ピッキング用の頂点座標**

プロキシが `SkinnedMeshRenderer` の場合は `BakeMesh()` を用いる。プロキシの `sharedMesh` と `bones` の両方が反映されたスキニング済み頂点配列が得られるため、上記 2 種類の効果がまとめて反映される。Unity 組み込みなので高速。

**ドラッグ量 → メッシュローカルデルタの変換**

ドラッグはワールド空間で発生し、デルタはメッシュローカル空間で保存する（8.4）。この間の変換が必要になる。

頂点 v のスキニング行列を、ボーンウェイト wᵢ、ボーン行列 Bᵢ、バインドポーズ Pᵢ から

```
M_v = Σ wᵢ · (Bᵢ · Pᵢ)
```

として求め、ワールド変位を逆変換する。

```
localDelta = M_v⁻¹ · worldDelta      （平行移動成分を除いた線形部）
```

**コスト**：`M_v` の算出はドラッグの影響半径内の頂点に対してのみ行えばよく、ドラッグ中はボーンが動かないためドラッグ開始時に一度計算してキャッシュできる。全頂点分を毎フレーム計算する必要はない。

`MeshRenderer`（非スキンメッシュ）の場合、`M_v` は Transform の `localToWorldMatrix` そのものになり、処理は自明に縮退する。

### 9.4 書き込み経路：プレビューへの反映

こちらは公開 API のみで実装できる。リフレクションは使わない。

```csharp
class DenMeshEditorPreviewFilter : IRenderFilter
{
    public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context) { ... }

    public Task<IRenderFilterNode> Instantiate(
        RenderGroup group,
        IEnumerable<(Renderer, Renderer)> proxyPairs,
        ComputeContext context) { ... }
}
```

- `GetTargetGroups` では `context.GetComponentsByType<DenMeshEditor>()` で対象を収集する
- メッシュの生成（複製 + デルタ加算）とプロキシへの代入は **`OnFrame` で行う**（理由は 9.2.1）
  - **新規インスタンスを作ること。**`IRenderFilter` の規約であり、`Dispose` で破棄する責任も負う
  - 生成は世代番号が変わったときだけ。毎フレーム作り直してはいけない
- 編集値の変更を検知させるため、`context.Observe(component, ...)` でコンポーネントを監視する
- ノードの `WhatChanged` は `RenderAspects.Mesh` を返す
- フィルタは NDMF パスに `.PreviewingWith(new DenMeshEditorPreviewFilter())` で登録する

#### 9.4.1 スケーラビリティ：`Instantiate` でシーンを走査しない

**大量配置しても重くならないことは本ツールの最重要要件**であり、そこを壊す最大の落とし穴がここにある。

`IRenderFilter.Instantiate` は**グループごと**（＝編集対象の Renderer ごと）に呼ばれる。ここで素直に `context.GetComponentsByType<DenMeshEditor>()` を呼んで全コンポーネントを `Observe` すると、シーン上のコンポーネント数 N に対して次のコストが生じる。

| 経路 | NDMF 側の実装 | 結果 |
| --- | --- | --- |
| `GetComponentsByType` | キャッシュ無し。全シーンルートを `GetComponentsInChildren` で走査（`GlobalQueries.cs:52`） | グループ数 × シーン全走査 |
| 同上（監視登録） | 全シーンルートに `MonitorGetComponents(root, ctx, true)` | シーンのどこでコンポーネントが増減してもノードが無効化される＝ノード再利用が効かない |
| `Observe(component, extract, …)` | `PropertyMonitor.CheckAllObjectsLoop` が**毎フレーム**全リスナーを Fire し、`extract` を再評価（`PropertyMonitor.cs:86`） | リスナー数 N × N。しかもロード時だけでなく**待機中も継続** |

つまり編集していない状態でも `O(N² × 編集頂点数)` が毎フレーム回り続ける。NDMF は 1 フレームあたり 2ms でタイムスライスして翌フレームへ持ち越すのでエディタは固まらないが、**メインスレッドを常時食い潰し、変更検出も遅延する**という形で現れる。

対策は、**対象 Renderer に関係するコンポーネントを `GetTargetGroups` の時点で確定させ、`RenderGroup.WithData` でグループに添付して渡す**こと。

```csharp
// GetTargetGroups：Renderer → その Renderer を対象にしているコンポーネント
builder.Add(RenderGroup.For(target).WithData(ownersOf[target]));

// Instantiate：シーン走査も、無関係なコンポーネントの監視も行わない
var components = group.GetData<List<DenMeshEditor>>();
foreach (var c in components) ObserveEdits(context, c);
```

`RenderGroup<T>` は `IEnumerable` な Context を `SequenceEqual` で比較する（`IRenderFilter.cs:121-131`）ため、**対象コンポーネントの構成が変わればグループの同一性も変わり、ノードが自動的に作り直される。**「B が新たに R を対象に加えた」ようなケースも取りこぼさない。

監視も 2 段に分ける。

| 監視する場所 | 見る値 | 変化したときに起きること |
| --- | --- | --- |
| `GetTargetGroups` | 対象 Renderer の集合 + 編集の有無 | グループ分割のやり直し |
| `Instantiate`（グループ内のコンポーネントのみ） | `vertexCount` / `Count` / `revision` | **そのグループのノードだけ**が作り直される |

これにより、あるコンポーネントを編集しても、それが触っていない Renderer のノードは NDMF 側で完全再利用され（`NodeController.Refresh` の `changes == 0 && !IsInvalidated` 経路）、メッシュの複製が走らない。

#### 9.4.2 上流メッシュの読み直しは間隔を空ける

`OnFrame` で上流メッシュの in-place 書き換えを検出するには `Mesh.GetVertices` で読み直すしかないが、これは全頂点のコピーである（5 万頂点なら 1 Renderer あたり 600KB/frame）。編集済み Renderer の数だけ毎フレーム走らせると、**編集していない待機中もシーン全体が重くなる。**

自分の編集内容の変化は `LiveEdits.Version` で即座に拾えるので、**上流側の検出だけ**を 0.2 秒間隔まで落とす。Renderer ごとに位相をずらし、読み直しが同じフレームに集中しないようにする。

#### ドラッグ中の反映：コンポーネントを書き換えない

`Observe(component)` による無効化は `ObjectChangeEvents` 経由であり、**コンポーネントを dirty にするとプレビューパイプライン全体が再構築される**。ドラッグ中に毎フレーム `Undo.RecordObject` + `SetDirty` を行うと、フレームごとにメッシュ複製が走って実用にならない。

そこで書き込みを 2 経路に分ける。

| タイミング | 書き込み先 | プレビューの更新契機 |
| --- | --- | --- |
| ドラッグ中（毎フレーム） | `LiveEdits`（Editor 内の静的な一時領域） | 世代番号 `LiveEdits.Version` の変化 |
| マウスを離したとき（1 回） | コンポーネント（`Undo.RecordObject` → `SetFrom` → `SetDirty`） | 同上（下記 9.4.3） |

`GatherEdits` は各 `MeshEdit` について「未確定データがあればそちらを優先」する。確定時に `LiveEdits.Clear()` するため、同じ結果へ滑らかに引き継がれる。

#### 9.4.3 編集セッション中はコンポーネントを監視しない

確定（マウスを離したとき）で `EditsFingerprint` が変われば、それだけで NDMF はパイプライン全体を作り直す。**これは 1 ドラッグにつき 1 回なら許容できるが、Undo では許容できない。**

パイプライン再構築 1 回のコストは、プロキシオブジェクトの再生成 + 全プレビューフィルタの再実行 + `Object.Instantiate(mesh)` である。高頂点数のアバターでは数百 ms に届く。Ctrl+Z を押しっぱなしにすると Undo がフレームごとに届き、そのたびに再構築が走るため、**そのままエディタのフリーズになる**。

そこで、**編集セッション中のコンポーネントは監視対象から外す**（`ObserveEdits` が `ActiveComponent` と一致したら `Observe` せずに戻る）。

| | セッション中 | セッション外 |
| --- | --- | --- |
| 更新の合図 | `LiveEdits.Version`（`EditSession` から明示的に出す） | `EditsFingerprint` の変化 |
| 更新の実体 | 生成済みメッシュへ `SetVertices`（`UpdateVertices`） | パイプライン再構築 |

セッション中に「コンポーネントを書き換えたのにプレビューが追従しない」ことが無いよう、確定・Undo / Redo のどちらの経路も `LiveEdits.Clear()` + `Invalidate()`（`EditSession.ClearLiveEdits`）を通す。`GatherEdits` は未確定データが無ければコンポーネントを読むので、これだけで巻き戻った内容が反映される。

セッションの開始・終了は `ActiveComponent`（`PublishedValue`）の変化として `GetTargetGroups` / `ObserveEdits` の両方が拾うため、**終了時には必ず通常の監視へ戻る**。同じ理由で `ShapeFingerprint` もセッション中は `HasEdits` を見ない — セッション中はデルタが空の対象もグループへ入れておりグループ分割に影響しない一方、「最初の 1 頂点を動かした瞬間」や「編集が空に戻る Undo」で全体が作り直されてしまうため。

#### Undo の粒度：ハンドルを掴んで動かす 1 動作 = 1 段

書き込みがマウスを離したときの 1 回だけになったことで、`Undo.RecordObject` と書き換えが同一フレームで完結する。`CollapseUndoOperations` でドラッグ中の記録をまとめる必要はない。

ただし**それだけでは足りない。**Unity は同じ Undo グループに入った `RecordObject` を 1 段にまとめるため、グループを切らないと**複数回のドラッグが 1 段に潰れ**、Ctrl+Z で一気に巻き戻る。グループが自動で進むのは限られたタイミングだけで、シーンビュー上の独自ドラッグでは進まない。

そこで確定処理を次の形にする。

```csharp
Undo.IncrementCurrentGroup();               // 直前の操作と切り離す
Undo.SetCurrentGroupName("Dennoko Mesh Editor");

Undo.RecordObject(_component, "Dennoko Mesh Editor");
… 半径とデルタを書き込む …
EditorUtility.SetDirty(_component);

Undo.FlushUndoRecordObjects();              // 差分をこの場で確定
Undo.IncrementCurrentGroup();               // 後続の無関係な操作を混入させない
```

- **`FlushUndoRecordObjects`** — `RecordObject` の差分は通常 MouseUp 直後に自動確定するが、その MouseUp は `Handles.PositionHandle` が既に消費している。自動フラッシュのタイミングに頼らない
- **空の段を作らない** — 書き込むものが無ければグループを切らずに抜ける。空の段が積まれると、Ctrl+Z を押しても何も起きないように見える
- **オーバーレイの設定変更も同様** — スライダーのドラッグは毎フレーム変更を出すので `CollapseUndoOperations` で 1 段にまとめるが、その基準グループを取る前にも `IncrementCurrentGroup` が要る。これが無いと連続した設定変更どうしが潰れる

Undo / Redo 後は `Undo.undoRedoPerformed` から `ResyncFromComponent` を呼び、作業状態（`Working` / `Snapshot`）をコンポーネントの現在値から作り直す。これを怠ると、巻き戻ったコンポーネントに古い作業状態が残り、次のドラッグの確定で「取り消したはずの編集」を書き戻してしまう。

#### Undo 連打を軽くする

`ResyncFromComponent` は `Refresh(true)` と作業状態の作り直しを含むため、Undo コールバックの中で毎回同期実行するのは重い。**Ctrl+Z の押しっぱなしは同一フレームに複数回届きうる**ので、コールバックではフラグを立てるだけにして、実処理は `EditorApplication.update` 側で 1 フレーム 1 回にまとめる（`_resyncPending`）。描画ループの外側で走るという点は `OnEditorUpdate` の再描画要求と同じ理由による。

作業状態の作り直しでは辞書インスタンスを作らない。`MeshEdit.CopyTo` / `CopyDeltas` で中身だけを入れ替える。編集頂点数が数万に達すると `ToDictionary` の確保が Undo 1 回ごとに数 MB 単位のゴミになり、連打で GC が跳ねるため。`Working` はインスタンスのまま `LiveEdits` へ渡してあるので、作り直さないこと自体が「公開済みの参照が古くならない」保証にもなる。

なお、Undo 連打で最も重いのはパイプライン再構築であり、それは 9.4.3 で断ってある。

#### Undo でハンドル位置も戻す

作業状態を戻すだけでは、**移動ハンドルがドラッグ後の位置に取り残される。**しかし `WorldVertices` から取り直すこともできない — Undo 直後の時点では NDMF のプレビューメッシュがまだ巻き戻っておらず（パイプライン再構築は非同期）、古い形状を読んでしまうため。

そこで**プレビューを読まずにハンドル位置を決められる形**にしておく。ハンドル位置は常に次で表せる。

```
handlePosition = baseWorld + skin · delta
```

`baseWorld` は選択頂点の「自分のデルタを除いた」ワールド位置、`skin` は選択時のスキニング行列。どちらも Undo では変化しない（Undo が変えるのはデルタだけ）ので、選択時に控えておけば、巻き戻ったデルタを入れるだけで正しい位置が出る。

もう一点、**選択そのものが落ちる**経路がある。Undo は `List<MeshEdit>` をシリアライズ経由で復元するため `MeshEdit` のインスタンスが差し替わることがあり、そうなると `SyncTargetList` が参照比較で不一致を検出して対象を作り直し、`ClearSelection` を呼ぶ。Undo で変わるのはデルタだけで「どの頂点を掴んでいるか」は変わらないので、(Renderer, 頂点番号) を控えておいて選び直す。

**フィルタ順序**：Scale Adjuster より後に評価される必要がある。NDMF パスの宣言で `.AfterPlugin("nadena.dev.modular-avatar")` 等により順序を明示する。

### 9.5 ビルド時適用

NDMF プラグインとして **`BuildPhase.Transforming` フェーズ**で処理する（理由は 8.3）。

- 元メッシュを複製し、デルタを加算（`MeshDeltaApplier` を共有）
- ボーンウェイト、既存シェイプキー、UV、バインドポーズは保持する
- **法線・接線の再計算は行わない。**元の値をそのまま保持する
  - 再計算するとシェーディングが変わり、特に lilToon などでの見た目が変化するため
  - 大きく変形させた場合に陰影が形状へ追従しないのは仕様として受け入れる
- 処理後、`DenMeshEditor` コンポーネントをビルド成果物から削除する（VRChat のアップロード制約回避）

---

## 10. ベイク処理

編集モードに依存せず、コンポーネントから単独で実行できること。

### 出力仕様

| 項目 | 仕様 |
| --- | --- |
| 保存先 | 元メッシュと同一フォルダ |
| 名前 | `<元メッシュ名>_edited`（既に `_edited` 付きなら追加しない） |
| 衝突時 | 末尾に `スペース + 連番`（`Body_edited 1`, `Body_edited 2` …） |
| シェイプキー化 | チェックボックスで選択。ON なら元形状を保持し編集分をシェイプキーとして追加 |
| Renderer 差し替え | **行わない。**アセット出力のみ |

### 注意点

- 元メッシュが FBX 内のサブアセットである場合、書き出しは新規 `.asset` として行う（FBX を書き換えない）
- 法線・接線はビルド時と同様に再計算しない
- シェイプキー化 ON の場合、シェイプキー名も `_edited` 系の規則に揃える
- 出力後は出力先パスを Console と Inspector に表示し、`EditorGUIUtility.PingObject` で選択させる（差し替えを手動で行うため、見つけやすさが必要）

---

## 11. 非機能要件

- **ロードコスト：** 外部 dll に依存しない。ドメインリロード時の初期化を最小限に保つ
- **編集レスポンス：** 数万頂点規模のメッシュでハンドル操作が引っかからないこと。影響頂点探索は空間分割（グリッド等）で前処理する
- **メモリ：** 編集セッション中のみ作業用バッファ（`BakeMesh` 結果、スキニング行列キャッシュ、空間分割グリッド）を保持し、終了時に解放する
- **安全性：** 元メッシュアセットへは決して書き込まない。ベイクのみが唯一のディスク書き出し経路
- **Undo：** Unity の Undo システムに統合する（編集操作 1 回 = Undo 1 ステップ）
- **エラー耐性：** NDMF が無い／プレビューが無効／頂点数不一致、いずれの場合も例外を出さずに縮退動作する

---

## 12. 決定事項

| # | 項目 | 決定 |
| --- | --- | --- |
| 1 | デルタの持ち方 | 頂点ごとのデルタを加算適用。上流の変形内容を一切推論しない（→ 8） |
| 2 | プロポーショナル距離基準 | ユークリッド距離。測地距離は非対応 |
| 3 | ミラー方式 | 仮想中心方式。中心 C と変位 D の双方を反射し、反対側でも同じ半径・減衰で適用（→ 5.2） |
| 4 | 法線・接線 | 再計算しない。元の値を保持 |
| 5 | ベイク | アセット出力のみ。Renderer の自動差し替えはしない |
| 6 | 対象指定 | 複数 Renderer 指定可。Renderer 保持オブジェクトへの付与時は `Reset()` で `targets[0]` に自動設定 |
| 7 | NDMF 依存 | **公開 API のみ。リフレクション不使用。**`IRenderFilterNode.OnFrame` でプロキシを受け取り `ProxyRegistry` 経由でシーンビューへ渡すプッシュ型構成（→ 9.1, 9.2） |

---

## 13. 実装状況

初版実装済み。Roslyn による型チェックは通過済み（警告レベル 4 でゼロ警告）。**Unity 上での実動作は未検証。**

### ファイル構成

```
Runtime/
  DenMeshEditor.cs              … コンポーネント、MeshEdit、FalloffType、MirrorAxis
Editor/
  ProxyRegistry.cs              … プロキシ Renderer の受け渡し
  LiveEdits.cs                  … ドラッグ中の未確定デルタの受け渡し（→ 9.4）
  MeshDeltaApplier.cs           … デルタ適用の共通ロジック
  DenMeshEditorPreviewFilter.cs … IRenderFilter / IRenderFilterNode
  DenMeshEditorPlugin.cs        … NDMF ビルドパス
  EditSession.cs                … シーンビュー編集
  FalloffUtil.cs                … 減衰カーブ
  DenMeshEditorInspector.cs     … Inspector UI
  DenMeshEditorBaker.cs         … ベイク
```

### 実装で確定した項目

| 項目 | 決定 |
| --- | --- |
| 中心線 ε | `max(1e-4, brushRadius * 0.05)`。半径への相対値とし、ブラシサイズに追従させた |
| 減衰プリセット | スムーズ（smoothstep）／直線／シャープ（二乗）／一定 の 4 種 |
| 影響頂点探索の空間分割 | **不要と判断し実装しない。** 探索は選択時とホイールでの半径変更時だけで、二乗距離での足切りを入れた O(V) の総当たりで十分速い。ドラッグ中の各フレームはキャッシュした重みを使うだけなので探索が発生しない |
| 遮蔽三角形の空間分割 | **実装する。** こちらはマウス移動のたびに走るため O(三角形数) が効く。スクリーン空間のハッシュグリッド（CSR 形式）を視点・形状の変化時にだけ構築する（→ 最適化 第 3 版） |
| ホイールでの半径変更 | 1 目盛り 1.05<sup>3</sup> ≒ 16%（`Event.delta.y` は 1 目盛り ±3）。範囲は 0.001〜0.5 m でオーバーレイのスライダーと共通 |
| ピッキングの遮蔽判定 | レイ × 三角形（両面）。裏面カリングは使わない。候補は近い順に最大 8 件。遮蔽物は編集対象の Renderer のみ（→ 6.2） |
| フィルタ順序 | `.AfterPlugin("nadena.dev.modular-avatar")` のみ。AAO はフェーズが後（Optimizing）なので指定不要。`AfterPlugin` は `WeakOrder` 制約かつ名前ベースの placeholder を使うため、MA 未導入でもエラーにならない |
| 頂点順序変更の検知 | `vertexCount` 照合のみ。記録するのは**プロキシの頂点数**とする（元メッシュの頂点数を記録すると、上流で頂点数が変わった場合にチェックを素通りして誤った頂点が動く） |
| ドラッグ終了の検知 | `GUIUtility.hotControl == 0` または `Event.rawType == MouseUp`。`Handles.PositionHandle` が MouseUp を `Use()` するため `Event.type` では検出できない（→ 修正履歴） |
| プレビュー対象の絞り込み | 編集セッション中はデルタが空の Renderer も対象に含め、それ以外は編集を持つ Renderer だけに絞る。セッションの開始・終了は `PublishedValue<DenMeshEditor>` で NDMF へ通知する |
| 編集データの保持形式 | `byte[]` 1 本（16 バイト/頂点）+ `revision` カウンタ。ロード時間・Prefab オーバーライド件数・変更検出コストのすべてに効く（→ 8.1.1）。旧形式は初回アクセス時に自動移行 |
| コンポーネント監視 | `context.Observe(component, extract, compare)` を使い、**グループに関係するコンポーネントだけ**を、**`revision` だけ**見て監視する。引数なしの `Observe(component)` は比較関数が常に false のため `brushRadius` を触っただけで全体が再構築される。また抽出関数は毎フレーム再評価されるので、監視範囲と抽出コストの両方を絞る必要がある（→ 9.4.1） |
| 上流メッシュの変化検出 | `Mesh.GetVertices(List)` で読み直して 64 点のサンプルを比較する。上流がメッシュを in-place で書き換えるとインスタンス比較では検出できないため。ただし読み直しは全頂点コピーなので、`LiveEdits.Version` が動いていないときは 0.2 秒間隔まで落とす（→ 9.4.2） |

### 設計からの差分

- **矩形選択は未実装。** クリック選択のみ。操作モデル (C, r, f, D) はクリック選択で完結するため、機能上の欠落にはならない
- ドラッグ確定後の再スナップショットは、プレビュー再構築の完了を待たずに「ハンドルの現在位置」を次の基準にする。プレビュー更新が非同期であることにタイミング依存しないための措置

### 修正履歴

**プレビューにメッシュ変形が反映されない不具合（初版）**

初版は `IRenderFilter.Instantiate` の中でプロキシの `sharedMesh` を編集済みメッシュへ差し替えていた。しかし `ProxyObjectController.OnPreFrame` がフレーム冒頭で `sharedMesh` を元 Renderer のものへ戻すため、この代入は描画前に必ず破棄されていた（詳細と修正方針は 9.2.1）。

併せて、ドラッグ中の毎フレーム `SetDirty` によるパイプライン再構築を廃止し、未確定デルタは `LiveEdits` 経由で渡す構成に変更した（→ 9.4）。

**ドラッグ中に Esc を押すと編集ツールが固まる不具合（初版）**

`ClearSelection` が `_dragging` を降ろしていなかったため、ドラッグ中に Esc で選択解除すると `_dragging` が立ちっぱなしになり、`Refresh` が二度と走らなくなっていた（頂点位置が更新されず、以降のピッキングも古い座標で行われる）。`ClearSelection` で降ろすよう修正。

---

#### レビュー指摘による修正（第 2 版）

**［致命的］ドラッグ結果がコンポーネントへ書き込まれない**

`HandleDrag` が `Event.current.type == EventType.MouseUp` で確定処理を起動していたが、`Handles.PositionHandle` は hotControl を持った状態で MouseUp を受けると `evt.Use()` を呼ぶ。`Event.current` は同一インスタンスなので、ハンドルから戻った時点で `type` は `EventType.Used` になっており、この条件は**永久に成立しない**。

結果として `Commit` が呼ばれず、`_dragging` が立ちっぱなしで `Refresh` も止まり、編集終了時に `Cleanup` の `LiveEdits.Clear()` で編集内容が消えていた（プレビューには出るのに保存されない）。`GUIUtility.hotControl == 0` と `Event.rawType == MouseUp` の両方で検出するよう修正。

**［高］エディタのライフサイクルが未処理**

ドメインリロード・プレイモード遷移・シーン切り替えでセッションが閉じられていなかった。`BakeScratch`（`HideAndDontSave` な Mesh）がエディタ再起動まで残り、`Tools.hidden` も戻らず、Enter Play Mode Options でドメインリロードを無効にしている環境ではプレイモード中もセッションが動き続けていた。`[InitializeOnLoadMethod]` で `AssemblyReloadEvents.beforeAssemblyReload` / `playModeStateChanged` / `sceneClosing` を購読して `End()` する。プレビュー用の生成メッシュも `GeneratedMeshTracker` でリロード時に破棄する。

**［高］Undo / Redo でセッション状態が同期しない**

`Undo.undoRedoPerformed` を購読していなかったため、巻き戻ったコンポーネントに対して古い `Working` / `Snapshot` が残り、次のドラッグで「取り消したはずの編集」を書き戻す可能性があった。`ResyncFromComponent` で作業状態を作り直す。

**［高］頂点数不一致がサイレント失敗していた**

`GatherEdits` が不一致の編集を捨てたうえで `SetFrom` で `vertexCount` を書き直して返すため、後続の `IsCompatible` チェックは常に成功し、警告が到達不能なデッドコードになっていた。`GatherEdits` にスキップ理由を返させ、ビルド時に必ず警告を出す。

**［中］プレビューパイプラインの過剰な再構築**

`context.Observe(component)` は比較関数が常に false（NDMF `SingleObjectQueries.cs`）なので、`brushRadius` のスライダーをドラッグすると毎フレーム全メッシュを複製し直していた。編集データのハッシュだけを監視するよう変更。併せてオーバーレイの設定変更は `Undo.CollapseUndoOperations` で 1 ドラッグ 1 段にまとめる。

**［中］その他**

- `SkinMatrix` がプロキシ破棄時に `MissingReferenceException`（`BuildInfluences` はホイール操作から `Refresh` を経由せずに呼ばれる）
- `DrawOverlay` の GUILayout 構成が Layout / Repaint 間で変化しうる（`AnyFallback` を Layout 時に固定）
- ベイクが `OnInspectorGUI` 中に `Selection` を変更し、編集セッションを終了させていた（`delayCall` へ退避）
- アバタールート配下でない Renderer をビルド時に書き換えうる（クローンに含まれないため実データが壊れる）→ `IsChildOf` で検証
- `Layout` イベント内の `Repaint()` による無限再描画 → `EditorApplication.update` から 10fps で要求
- 頂点スクリーン座標を視点・形状が変わらない限り再計算しない（マウス移動のたびの O(V) を削減）
- ビルド成果物に `ObjectRegistry.RegisterReplacedObject` / `AssetSaver.SaveAsset` を追加
- Read/Write 無効メッシュの検出と警告
- `Tools.hidden` を開始前の値へ戻す、`Handles.PositionHandle` を `Tools.handleRotation` に追従させる
- `ProxyRegistry.Prune()` で破棄済みエントリを掃除

---

#### 最適化（第 3 版）— 大量配置時のコストを潰す

「シーン上にこのコンポーネントを大量に置いてもロードが伸びず、重くならない」を最重要要件として全体を見直した。NDMF 1.11.0 のプレビュー基盤の実装（`ProxyPipeline` / `NodeController` / `TargetSet` / `PropertyMonitor` / `GlobalQueries`）を追って裏を取っている。

**［最優先］`Instantiate` の全コンポーネント走査による O(N²)**

グループごとにシーンを全走査し、全コンポーネントを `Observe` していた。NDMF は監視値の抽出関数を**毎フレーム**再評価するため、待機中も `O(N² × 編集頂点数)` が回り続けていた。`RenderGroup.WithData` で関係コンポーネントをグループに添付する構成へ変更（→ 9.4.1）。あわせて、あるコンポーネントの編集が無関係な Renderer のノードを無効化しなくなった。

**［最優先］フィンガープリントが編集頂点数に比例**

`EditsFingerprint` が全インデックスと全デルタを走査していた。これも毎フレーム評価される。`MeshEdit.revision`（シリアライズ済みカウンタ）を見るだけの O(編集対象数) に変更（→ 8.1.1）。

**［最優先］`OnFrame` の毎フレーム全頂点コピー**

上流メッシュの in-place 書き換え検出のために、編集済み Renderer 全部について毎フレーム `Mesh.GetVertices` していた。自分の編集の変化は `LiveEdits.Version` で拾えるので、上流側の検出だけ 0.2 秒間隔に落とし、Renderer ごとに位相をずらした（→ 9.4.2）。

**［高］デルタのシリアライズ形式**

`List<int>` + `List<Vector3>` を `byte[]` 1 本へ変更。シーン／Prefab のロード時間と、Prefab オーバーライドの件数に直接効く（→ 8.1.1）。旧形式は初回アクセス時に自動移行する。

**［中］プレビュー更新パスの全頂点走査**

`UpdateVertices` が「基準頂点のコピー → デルタ加算 → SetVertices → RecalculateBounds」で頂点配列を 3〜4 周していた。基準配列に直接書き込み、アップロード後に**書き込んだ頂点だけ元の値へ戻す**方式に変更（float の加減算は可逆でないため、引き算ではなく退避・復元にする）。全頂点分の作業バッファも不要になった。バウンズは上流のバウンズを最大デルタ長ぶん膨らませて代用する（保守的に大きくなるだけなのでカリング上は安全）。

**［中］編集セッション中のコスト**

| 箇所 | 変更 |
| --- | --- |
| `_geometryGeneration` | `Refresh` のたびに無条件で ++ していたため、スクリーン座標キャッシュが秒 10 回必ず捨てられていた。**頂点位置が実際に変化したときだけ**進める |
| `UpdateWorldVertices` | `Mesh.vertices` / `BakeMesh().vertices` が毎回 `Vector3[]` を確保していた → `GetVertices(List)` で使い回す |
| ボーンウェイト・バインドポーズ・三角形 | パイプライン再構築のたびに配列を確保し直していた → `GetBoneWeights(List)` / `GetBindposes(List)` / `GetTriangles(List, submesh)` |
| `CollectNearbyTriangles` | ピック 1 回（＝マウス移動 1 回）ごとに全三角形を走査していた → スクリーン空間のハッシュグリッド（CSR 形式）を視点・形状の変化時にだけ構築し、クリック位置のセル 1 つを引く。外接矩形を 24px 広げて登録してあるため取りこぼさない |
| 頂点のスクリーン射影 | `HandleUtility.WorldToGUIPoint` は Handles.matrix と GUI クリップを経由して重い。**代表点 2 つで照合して一致した場合だけ**自前射影に切り替える（前提が崩れていれば従来経路のまま） |
| `BuildInfluences` | 影響圏外の頂点にも平方根を計算していた → 二乗距離で弾いてから `Mathf.Sqrt` |
| `ApplyDisplacement` | ドラッグ中、毎フレーム `Dictionary` を新規確保していた → インスタンスを使い回して中身だけ入れ替える |
| `GeneratedMeshTracker` | 登録解除が `List.Remove`（O(n)）→ `HashSet` |
| `ProxyRegistry` | `Prune()` がセッション終了時にしか走らず、破棄済みエントリが溜まりうる → 件数のしきい値で自動掃除 |

**採用しなかった案：Burst / Compute Shader**

第 1 章の「重い dll 依存を持たない」という方針に真っ向から反するため、Burst・Collections パッケージの導入は見送る。Compute Shader も、NDMF のプロキシパイプラインが CPU 側 `Mesh` を前提にしており、`BakeMesh`・ピッキング・ベイクとの整合と「プレビューとビルドで同じコードを共有する」という担保が崩れるため採らない。

追加依存ゼロで使える Job System（`Unity.Jobs` / `NativeArray` は `UnityEngine.CoreModule` に含まれる）は将来の選択肢として残るが、効くのは編集セッション中の応答性だけで、最重要要件である待機時・ロード時のコストには寄与しない。

#### Inspector での設定変更がセッションへ届かない不具合

ミラー軸を切り替えても、シーンビューのミラー中心（ピンクの円）が古い軸のまま更新されなかった。対象を選び直すと直る、という症状。

原因は通知経路の欠落。オーバーレイのスライダーとミラーのトグルは、変更を検出した場所で `RecomputeMirrorCenter` / `BuildInfluences` を直接呼んでいたが、**Inspector の `PropertyField` 経由の変更にはその経路が無かった**。`ミラー軸` は Inspector にしか無いため、`serializedObject.ApplyModifiedProperties()` でコンポーネントの値だけが変わり、セッションが持つ `_mirrorCenterWorld` と `_influences` は選択時のまま取り残されていた（`BeginSelection` が両方を計算し直すので、選択のやり直しで直って見えた）。

個々のフィールドに変更検出を足すのではなく、`OnInspectorGUI` の末尾で一括して拾う。

```csharp
if (serializedObject.ApplyModifiedProperties() && EditSession.IsActive(component))
{
    EditSession.NotifySettingsChanged();
}
```

`ApplyModifiedProperties` は「実際に書き換わったか」を返すので、これで Inspector 由来の変更が漏れなく届く。半径・減衰など後から増えた設定にも自動で追従する。`NotifySettingsChanged` 側は選択が無ければ何もしないため、非選択時の空振りコストも無い。

#### 表示の遮蔽と Undo 連打（第 4 版）

**［高］Undo 連打でエディタが固まる**

高頂点数のメッシュを何度か編集したあと Ctrl+Z を連打すると重くなる、という報告。原因は 1 つではなく段階がある。

| 段階 | 内容 | 対策 |
| --- | --- | --- |
| 支配的 | Undo で `EditsFingerprint` が変わり、**Undo 1 回ごとに NDMF のパイプライン全体が再構築**されていた（プロキシ再生成 + 全フィルタ再実行 + メッシュ複製） | 編集セッション中はコンポーネントを監視せず、`LiveEdits.Version` 経由の頂点書き換えだけで済ませる（→ 9.4.3） |
| 副次 | 上記の再構築でプレビューメッシュのインスタンスが毎回変わるため、`Refresh` の「メッシュが変わった」経路（バインドポーズ・ボーンウェイト・三角形の読み直し）も Undo ごとに走っていた | 上と同じ対策で自然に消える |
| 副次 | `ResyncFromComponent` が Undo コールバック内で同期実行され、`Dictionary` を 2 つ新規確保していた | `_resyncPending` で 1 フレーム 1 回にまとめ、`CopyTo` / `CopyDeltas` で辞書を使い回す |

`Undo.RecordObject` 自体のコスト（大きな `byte[]` を持つコンポーネントの差分計算）は未計測。上記で足りなければ `Undo.RegisterCompleteObjectUndo` への切り替えを検討する（差分計算を行わない代わりに、エントリごとにオブジェクト全体のスナップショットを持つ）。

**［中］影響範囲の表示に裏側の頂点が混ざる**

ブラシ半径は球なので、閉じた形状では必ず裏面の頂点が範囲に入り、表示がごちゃつく。深度バッファとの比較で落とす（→ 6.3）。表示は毎フレーム最大 3000 点に走るため、ピッキング側のレイ交差を流用する選択肢は無い。

#### 破棄済みプレビューメッシュによる確定漏れ（第 5 版）

**［致命的］ドラッグ終了時に編集が元の形状へ戻る**

`Commit` が「`target.Mesh == null` の対象は書き込まない」という条件で判定していた。`TargetState.Mesh` はプレビューパイプラインが所有するメッシュ（自分の生成メッシュか、下流フィルタの生成メッシュ）であり、パイプラインが作り直されると `Object.DestroyImmediate` で破棄される＝ Unity の null 比較で `null` になる。しかも `Refresh` はドラッグ中は止めているため、破棄済みの参照が確定時までそのまま残る。

下流フィルタ併用時（`DownstreamGuard` のラッチ成立時）は、ドラッグ中の更新を `LiveEdits.SyncedVersion` 経由のパイプライン再構築として流している（→ 9.4.3）。つまり**ドラッグのたびに必ずメッシュが作り直され、確定時には必ず破棄済み**になる。結果、`hasChanges` が false のまま `ClearLiveEdits()` だけが走り、未確定デルタが捨てられて元の形状に戻っていた（プレビューには出るのに保存されない）。

対策として、メッシュを読めたときに頂点数を `TargetState.VertexCount` へ控え、確定時は `ResolveVertexCount`（生きたメッシュ → 控えた頂点数 → 元 Renderer のメッシュ）で解決する。記録するのがプロキシの頂点数であること（→ 12 の決定事項）は維持される。

### 未検証項目（Unity 上で確認が必要）

1. Scale Adjuster 適用下で、シーンビューの頂点位置がスケール調整に追従すること
2. スキニング行列の逆変換により、ドラッグ量とメッシュの動きが一致すること（ボーンにスケールがかかった部位で特に）
3. ドラッグ中のフレームレート（`Mesh.SetVertices` によるフルアップロードが毎フレーム走る）
4. ドラッグ確定が 1 回のマウスリリースで行われること（上記の致命バグの回帰確認）
6. **旧形式データの移行**：`indices` / `deltas` を持つ既存シーンを開いて編集内容が保たれること
7. **`byte[]` の YAML 表現**：`blob` が 1 行の 16 進文字列として書き出されること（同プロジェクト内の `Mesh._typelessdata` と同じ形。実際にシーンを保存して目視確認する）
8. **Prefab オーバーライド**：Prefab インスタンス上で編集したとき、オーバーライドが `blob` 1 件にまとまること
9. **自前射影の校正**：`TryCalibrateFastProjection` が期待どおり成立するか（成立しなくても従来経路へフォールバックするだけだが、成立していないと高速化が効かない）
10. **遮蔽ハッシュグリッド**：ピック結果が従来と一致すること。特にカメラをメッシュへ極端に近付けた状態（オーバーフロー経路）と、カメラ面をまたぐ三角形がある状態
11. **表示の遮蔽（→ 6.3）**：裏側の頂点が消えること。あわせて (a) ドットが明滅しないこと、(b) ドラッグ中もドットが消えず変形に追従すること、(c) 薄い部位（スカート・髪）で手前の頂点まで消えていないこと、(d) ブラシ円がドット群と一緒に動くこと（ミラー円も含む）
12. **セッション中の監視除外（→ 9.4.3）**：ドラッグ確定・Undo・Redo でプレビューが正しく追従すること。特に**編集を 1 つも持たない状態からの初回編集**と、**全編集が空に戻る Undo**。編集終了後にコンポーネントの内容がプレビューへ反映されていること
13. **Undo 連打**：高頂点数メッシュで Ctrl+Z を押しっぱなしにしてもフリーズしないこと（改善量を Profiler で確認する。残るようなら `Undo.RegisterCompleteObjectUndo` を検討）
5. 編集開始時にプロキシが生成され、`ShowFallbackWarning` が誤って出ないこと

### 未対応

- `package.json`（VPM パッケージ化）は未作成。現状は `Assets/` 配下のツールなので、NDMF の下限バージョン宣言はパッケージ化のタイミングで行う

1 が想定通りに動かない場合は、記事のリフレクション手法（`PreviewSession.OriginalToProxyRenderer`）が**実測で動作確認済みの代替手段**として使える。その場合は `ProxyRegistry` の内部実装のみを差し替えればよく、シーンビュー側のコードは変更不要 — レジストリを挟む構成にしておいた理由の一つ。

### NDMF バージョン追従方針

`internal` API への依存を排したため、追従コストは大きく下がった。`IRenderFilter` / `IRenderFilterNode` はいずれも `[PublicAPI]` 属性付きで、破壊的変更があればコンパイルエラーとして検出される（サイレントに壊れない）。

- **サポート範囲を明示する。** `package.json` の依存に下限を書く。`IRenderFilter` の現行シグネチャが導入されたバージョンを特定する必要がある（開発時の検証は 1.11.0）
- **NDMF 更新時の回帰確認**は、Scale Adjuster を適用したアバターで、シーンビュー上の頂点位置がスケール調整に追従するかを見れば足りる

### 型チェックの再実行

Unity エディタを起動したままでも、Roslyn で型チェックだけを回せる（Library をロックしない）。

```bash
U="C:/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Data"
dotnet "$U/DotNetSdkRoslyn/csc.dll" -noconfig "@build.rsp"
```

参照が必要なもの：

- `$U/Managed/UnityEngine/*.dll`（ただし monolithic な `UnityEditor.dll` は型が重複するため除外）
- `$U/MonoBleedingEdge/lib/mono/unityjit-win32/` の `mscorlib` / `System` / `System.Core` / `Facades/netstandard.dll`
- `Packages/com.vrchat.base/Runtime/VRCSDK/Dependencies/Managed/System.Collections.Immutable.dll`
  （NDMF は v7 を要求する。Mono 同梱の v1.2.3 では `CS1705` になる）
- `Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/VRCSDKBase.dll`（`IEditorOnly` 用）
- `Library/ScriptAssemblies/nadena.dev.ndmf.dll` / `nadena.dev.ndmf.runtime.dll`
