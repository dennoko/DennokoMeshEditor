# DennokoMeshEditor — Unity 6 移行調査

- 調査日: 2026-09-06
- 現行: Unity 2022.3.22f1 / Built-in RP
- 目標: Unity 6 (6000.0 LTS) / **BiRP 維持**
- 共通調査: [`../../../Docs/Impl/unity6-migration-overview.md`](../../../Docs/Impl/unity6-migration-overview.md)

## 判定

🔍 **要検証** ＋ ⛔ **外部依存あり** — Unity 6 非対応の API は **0 件**。修正すべきコードはない。
UnityEditor 内部 API へのリフレクションが 1 箇所あり、Unity 6 上での実動作確認が必要。

## 構成

| 項目 | 内容 |
|---|---|
| 規模 | C# 32 ファイル / 約 7,097 行 |
| asmdef | `dennokoworks.DenMeshEditor.Editor`（→ Runtime, `nadena.dev.ndmf`, `nadena.dev.ndmf.runtime`）<br>`dennokoworks.DenMeshEditor.Runtime`（→ `VRC.SDKBase`） |
| エントリ | `MenuItem("GameObject/dennokoworks/Dennoko Mesh Editor")` |
| UI | カスタムインスペクタ（IMGUI）+ SceneView 編集セッション |
| 外部依存 | **NDMF**（asmdef 参照）、**VRChat SDK**（`VRC.SDKBase.IEditorOnly`） |

`versionDefines` により `DEN_MESH_EDITOR_VRCSDK` が定義される設計で、
SDK 未導入環境でもコンパイルが通るよう配慮されている。

DenLattice と設計・構成が近く、`SelectionOutline.cs` は同一実装。

## 検出事項

### 1. `UnityEditor.AnnotationUtility` へのリフレクション（🔍 要検証・本ツール最大のリスク）

`Editor/Session/SelectionOutline.cs:124-145`

```csharp
var type = typeof(EditorUtility).Assembly.GetType("UnityEditor.AnnotationUtility");
...
var property = type.GetProperty("showSelectionOutline",
                   BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
```

- `UnityEditor.AnnotationUtility.showSelectionOutline` は **Unity 本体の internal API**。
  メッシュ編集中に SceneView の選択アウトライン（オレンジ枠）を一時的に抑制するために使用。
- Unity 6 で改名・移動・削除されてもコンパイルエラーにならず、静かに解決失敗する。
- 実装は非常に防御的:
  - `typeof(EditorUtility).Assembly` を優先して探し、見つからなければ全アセンブリを走査（`:138-145`）
  - プロパティ型が `bool` で読み書き可能であることまで検証
  - **失敗をキャッシュしない**（ドメインリロード直後の未解決状態を覚え込まないための配慮）

**Unity 6 で起きること**: 例外は出ず、**編集中に選択アウトラインが出たままになる**だけ。
頂点編集そのものは動作するが、アウトラインが編集対象の頂点表示と重なって見づらくなる。

**対応**

1. Unity 6 上でメッシュ編集を開始し、選択アウトラインが消えるか目視確認する。
2. 解決失敗時に一度だけ警告ログを出す（本実装は失敗をキャッシュしない設計なので、
   毎フレーム出力しないようフラグ管理に注意する）。

> **同一実装が `DenLattice/Editor/Session/SelectionOutline.cs` にも存在する。
> 片方を修正したらもう片方も同じ修正を入れること。**

### 2. 頂点プレビューと SceneView 描画（✅ 影響なし）

`Editor/Session/EditSession.VertexPreview.cs:25`

```csharp
// シーンビューの描画結果（RenderTexture.active）を読み戻して下地の色を測る方法も
```

コメント内の記述であり、**実際には `RenderTexture.active` を読み戻していない**。
BiRP 依存の描画読み戻しを避けた設計になっており、Unity 6 の描画系変更の影響を受けない。

`Editor/Session/EditSession.Picking.cs:161`

```csharp
// WorldToGUIPoint は Handles.matrix や GUI クリップを経由するため単発でも重く、
```

`HandleUtility.WorldToGUIPoint` のコスト対策が既に施されている。Unity 6 で API 変更なし。

### 3. `SceneView.duringSceneGui`（✅ 影響なし）

`Editor/Session/EditSession.cs:120,141` — 登録／解除が対称。Unity 6 で API 変更なし。

### 4. `VRC.SDKBase.IEditorOnly`（⛔ 外部依存）

`Runtime/DenMeshEditor.cs:16`

```csharp
, VRC.SDKBase.IEditorOnly
```

- メッシュ編集コンポーネントをアップロード時にランタイムから除去するためのマーカー。
- asmdef の `versionDefines` で `DEN_MESH_EDITOR_VRCSDK` が定義される構成のため、
  SDK 未導入環境でもコンパイル可能。
- VRChat SDK の Unity 6 対応版を待つ。事前作業は不要。

### 5. NDMF 参照（⛔ 外部依存）

asmdef が `nadena.dev.ndmf` / `nadena.dev.ndmf.runtime` を参照。
**NDMF の内部 API へのリフレクションは本ツールには存在しない**ため、
NDMF の公開 API が維持される限り追従コストは低い。

### 6. バージョンチェッカー（✅ 影響なし）

`Editor/Version/DenMeshEditorVersion.cs:167`

```csharp
var inspectors = Resources.FindObjectsOfTypeAll<DenMeshEditorInspector>();
```

`Resources.FindObjectsOfTypeAll<T>()` は **Obsolete ではない**（共通調査 2.2 節の注記参照）。
`Object.FindObjectsOfType` と混同して置換しないこと。**修正不要。**

`Editor/Version/DennokoVersionChecker.cs:136` の `#if UNITY_2020_2_OR_NEWER` は
Unity 6 でも true。旧分岐が死にコードになるだけ。**修正不要。**

## 非該当の確認

| 確認項目 | 結果 |
|---|---|
| `Object.FindObjectsOfType` / `FindObjectOfType` | **なし**（`Resources.FindObjectsOfTypeAll` のみ = Obsolete 対象外） |
| `GraphicsFormat.DepthAuto` / `ShadowAuto` / `VideoAuto` | **なし** |
| UI Toolkit（`ExecuteDefaultAction` / `UxmlFactory` 等） | **なし**（IMGUI のみ） |
| IMGUI テーマ（共有 `EditorStyles` 書き換え） | **なし** |
| Compute シェーダ / カスタムシェーダ | **なし** |
| `Lightmapping` / 物理 API | **なし** |
| NDMF 内部 API へのリフレクション | **なし** |

## 移行手順

### フェーズ 1（Unity 2022.3.22f1 のまま実施可）

- [ ] `SelectionOutline.ResolveProperty()` の解決失敗時に警告ログを追加
      （毎フレーム出力しないようフラグ管理する。失敗をキャッシュしない設計は維持すること）
- [ ] 同じ修正を `DenLattice/Editor/Session/SelectionOutline.cs` にも適用
- [ ] `MenuItem("Tools/Your Tool Name")` というテンプレート由来の未整理メニュー項目の確認

### フェーズ 3（外部依存の Unity 6 対応後）

1. VRChat SDK の Unity 6 対応版を導入
2. NDMF の Unity 6 対応版を導入
3. 下記チェックリストで動作確認

> **先行検証について**: `VRC.SDKBase` は Runtime asmdef のみが参照しており、
> `versionDefines` でガードされている。SDK 未導入の Unity 6 環境でも
> コンパイルは通るため、**メッシュ編集機能そのものの先行検証は可能**。

## 検証チェックリスト（Unity 6）

### SDK/NDMF なしで確認できる項目（先行検証）

- [ ] コンパイルエラー・警告が 0 件
- [ ] `GameObject/dennokoworks/Dennoko Mesh Editor` からコンポーネントを追加できる
- [ ] カスタムインスペクタが正しく描画される
- [ ] **編集開始時に SceneView の選択アウトラインが消える**（`AnnotationUtility` リフレクションの確認）
- [ ] 編集終了時に選択アウトラインが元に戻る（設定の復元）
- [ ] 頂点のピッキングが正しい位置で反応する（`WorldToGUIPoint` 経路）
- [ ] 頂点プレビュー描画が正しく表示される
- [ ] 頂点の選択・移動・削除操作が反映される
- [ ] Undo / Redo が正しく動作する
- [ ] 大きめのメッシュでピッキングのパフォーマンスが劣化していない

### SDK/NDMF 対応版で確認する項目

- [ ] `IEditorOnly` によりアップロード時にコンポーネントが除去される
- [ ] NDMF ビルドパイプラインでメッシュ編集が適用される
- [ ] アバターのアップロードが成功する
