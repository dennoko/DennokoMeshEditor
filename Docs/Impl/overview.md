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
Assets/dennokoworks/DenMeshEditor/
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
- `DisplayName`：`Den Mesh Editor`

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
    public Renderer  target;
    public int       vertexCount;  // 整合性チェック用
    public int[]     indices;      // 動かした頂点のインデックス
    public Vector3[] deltas;       // 対応する移動量（メッシュローカル空間）
}
```

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
    internal static void Remove(Renderer original) => _map.Remove(original);

    internal static bool TryGet(Renderer original, out Renderer proxy)
        => _map.TryGetValue(original, out proxy) && proxy != null;  // Unity の null チェック必須
}

// フィルタノード側
public void OnFrame(Renderer original, Renderer proxy)
{
    ProxyRegistry.Report(original, proxy);
}

public void Dispose()
{
    // パイプライン再構築でプロキシは破棄される。登録も外す
    foreach (var original in _originals) ProxyRegistry.Remove(original);
}
```

シーンビューの編集ツールは `ProxyRegistry.TryGet` でプロキシを引き、そこから頂点位置を得る。

**寿命の注意点：** プロキシはパイプライン再構築のたびに破棄・再生成される。レジストリの参照は必ず Unity の null 比較（`proxy != null`）で検証してから使う。`IRenderFilterNode.Dispose` での登録解除と合わせて二重に守る。

**プレビュー無効／フィルタ未起動の場合：** `TryGet` が false を返す。この場合は元 Renderer の `sharedMesh` にフォールバックし、「他ツールの影響が反映されていません」と Inspector に警告表示する。編集自体は可能な状態を保つ。

#### 自分の編集分の二重適用について

我々のフィルタは `Instantiate` でデルタを適用済みのメッシュをプロキシに設定する。したがって `OnFrame` で受け取るプロキシの形状は **ベース + 自分のデルタ** になっている。

これは表示・ピッキングにとっては**正しい状態**（現在の編集結果そのもの）。新しいドラッグは既存デルタへの増分として加算されるため、ベース形状を別途保持する必要はなく、二重適用も起きない。

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
- `Instantiate` では渡されたプロキシの `sharedMesh` を複製し、デルタを加算した新規 Mesh を設定する
  - **新規インスタンスを作ること。**`IRenderFilter` の規約であり、`Dispose` で破棄する責任も負う
- 編集値の変更を検知させるため、`context.Observe(component, ...)` でコンポーネントを監視する
- ノードの `WhatChanged` は `RenderAspects.Mesh` を返す
- フィルタは NDMF パスに `.PreviewingWith(new DenMeshEditorPreviewFilter())` で登録する

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

## 13. 残課題

設計上の大枠は確定。以下は実装時のチューニング項目。

1. **中心線 ε の既定値** — ミラー適用をスキップする閾値（5.2）。アバターのスケールに対する相対値にするか絶対値にするか
2. **減衰カーブのプリセット選定** — 何種類用意するか。Blender 相当の全種は不要
3. **空間分割グリッドのセルサイズ** — 影響半径に対する比率で自動決定するのが妥当か
4. **フィルタ順序の指定方法** — `.AfterPlugin` で MA を名指しするか、`.BeforePlugin` で AAO を指定するか、両方か
5. **頂点順序変更の検知精度** — `vertexCount` 照合のみで足りるか、ベース頂点座標のハッシュも保存するか

### NDMF バージョン追従方針

`internal` API への依存を排したため、追従コストは大きく下がった。`IRenderFilter` / `IRenderFilterNode` はいずれも `[PublicAPI]` 属性付きで、破壊的変更があればコンパイルエラーとして検出される（サイレントに壊れない）。

- **サポート範囲を明示する。** `package.json` の依存に下限を書く。`IRenderFilter` の現行シグネチャが導入されたバージョンを特定する必要がある（開発時の検証は 1.11.0）
- **NDMF 更新時の回帰確認**は、Scale Adjuster を適用したアバターで、シーンビュー上の頂点位置がスケール調整に追従するかを見れば足りる

### 実装初期に検証すべきこと

本設計は NDMF 1.11.0 のソース読解に基づく。実装の最初のステップとして、**プッシュ型でプロキシが取得できることを最小構成で確認する**こと。

1. `DenMeshEditor` コンポーネントを持つ Renderer に対して `IRenderFilter` を登録する
2. `IRenderFilterNode.OnFrame` にログを仕込み、毎フレーム呼ばれること・`proxy` が非 null であることを確認する
3. Scale Adjuster を適用したアバターで `proxy` から頂点位置を取り、スケール調整が反映されていることを確認する

ここで想定通りに動かない場合は、記事のリフレクション手法（`PreviewSession.OriginalToProxyRenderer`）が**実測で動作確認済みの代替手段**として使える。その場合は `ProxyRegistry` の内部実装のみをリフレクションに差し替えればよく、シーンビュー側のコードは変更不要になる — レジストリを挟む構成にしておく理由の一つ。
