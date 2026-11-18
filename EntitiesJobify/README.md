# Entities Jobify

Entities Jobify は、`ISystem.OnUpdate` 内の `SystemAPI.Query` ループを解析し、そのループを `Run()`、`Schedule()`、または `ScheduleParallel()` で実行されるネストされた `IJobEntity` に置き換える Rider Quick Fix を提供します。

Unity `6000.6.8f1` と Entities `6.6.0` を対象としています。このアナライザー自体は Entities の依存関係を追加しないため、プロジェクトで既に Entities を使用している必要があります。

## インストール

Unity Package Manager から、以下の Git URL を使用してパッケージを追加します。

```text
https://github.com/seikasan/EntitiesJobify.git?path=/com.seikasan.entities-jobify
```

プロジェクトには `com.unity.entities` `6.6.0` が必要です。パッケージのインストール後に Rider でプロジェクトが更新されない場合は、Unity で **Assets > Open C# Project** を実行し、Rider でソリューションを再読み込みしてください。

## 使い方

パッケージの **Samples** リストから **Simple movement** または **Complex movement** をインポートします。サンプルのシステムを開き、`foreach` にカーソルを置いて **IJobEntity に変換（Run）** を適用します。Complex サンプルには、条件分岐、ベクトル計算、およびタグフィルターが含まれています。また、自動実行されずにコードを確認できるよう、システムには `DisableAutoCreation` が付与されています。

`Run()` は元のループの位置で同期的に実行されます。`Schedule()` と `ScheduleParallel()` は `state.Dependency` を介して処理を登録します。スケジュールされたループは通常、`OnUpdate` の末尾に配置されます。ECB を使用するループを `ScheduleParallel()` に変換した場合、その直後に `state.Enabled = false;` を続けることができます。スコープ付きの `NativeArray` をキャプチャする並列ループは、元のループの位置で完了を待機します。

対象の `ISystem` 型に `[BurstCompile]` が付いている場合、生成されるジョブにも `[BurstCompile]` が付与されます。`OnUpdate` メソッドのみに付与された属性は引き継がれません。生成される属性には Burst のデフォルトオプションが使用されます。実際に Burst コンパイルされるかどうかは、コードが Burst に対応しているか、および Unity プロジェクトで Burst が有効になっているかに依存します。

## サポートされているループの形式

アナライザーは、トップレベルの `partial` かつ非ジェネリックなシステム型において、実際の `ISystem.OnUpdate(ref SystemState)` 実装の直下にある `foreach` を対象とします。クエリには 1〜7 個の異なる `RefRO<T>` または `RefRW<T>` 要素を含めることができます。ループ本体では、コンポーネントへの書き込みを行うか、サポートされている ECB コマンドを記録する必要があります。要素が複数ある場合は、`var` によるタプル分解が必要です。`WithEntityAccess()` を使用する場合は、タプルの最後にエンティティを配置してください。クエリ対象のコンポーネント型は、`ISharedComponent` でも `IEnableableComponent` でもない、unmanaged かつ非タグの `IComponentData` 型である必要があります。`WithAll` と `WithNone` は、1 回の呼び出しにつき 1 つ以上の通常の `IEnableableComponent` ではないタグを受け付けます。

ループ本体では、初期化済みのスカラ型および `float2/3/4` 型のローカル変数、代入、インクリメント・デクリメント文、`if/else`、および三項演算子 `?:` がサポートされています。また、独自の `break` や `continue` を持つ内部の `for`、`while`、`do/while` ループもサポートされています。サポートされている式には、算術演算、比較演算、論理演算およびビット演算、`float2/3/4` のコンストラクタ、ならびに `math.clamp`、`math.lerp`、`math.dot`、`math.normalizesafe` などの一部の Unity Mathematics 関数が含まれます。

ループ本体からは、`ValueRO` または `ValueRW` を介してネストされたアンマネージドフィールドにアクセスできます。また、`RotateY`、`Translate`、`WithScale` などのメソッドを使用して `LocalTransform` 全体を代入することも可能です。`OnUpdate` 内の前方の直下の文で初期化されたアンマネージドなローカル変数は、値としてキャプチャできます。ループ内では、ネストされた構造体を介してそれらのスカラ型や `float2/3/4` 型のインスタンスフィールドを読み取ったり、キャプチャした構造体を同じ型のコンポーネントフィールドに代入したりできます。ref struct、ポインタフィールド、および `NativeContainer` フィールドはキャプチャの対象外です。

コードフィックスは、`NativeArray<T>` のインデクサアクセス、`Length`、および `foreach` をサポートしています。`OnUpdate` 内のクエリより前で、`Allocator.TempJob` または `Allocator.Persistent` を用いて `using var` で確保された配列は、`Run()` または `ScheduleParallel()` において `[ReadOnly]` フィールドを介して読み取ることができます。並列フィックス（`ScheduleParallel`）では、後続の文や Dispose の前にジョブの完了を待機します。また、ループ本体で `using var` と `Allocator.Temp` を使用して、スコープ付きの一時配列を確保することも可能です。その要素に書き込むには、配列を通常のローカル変数に代入し、そのローカル変数に対してインデックスアクセスを行ってください。

ループでは、`Instantiate`、`SetComponent<T>`、`AddComponent<T>`、`RemoveComponent<T>`、`DestroyEntity` のために `EntityCommandBuffer` をキャプチャできます。Spawner のループでは、`Allocator.Temp` を用いて `using` スコープで確保された `NativeArray<Entity>` を反復処理することも可能です。バッファが組み込みの ECB システムシングルトンから取得され、ループ内でのみ使用されている場合は、並列フィックス（`ScheduleParallel`）を利用できます。この場合、`ParallelWriter` と、ソートキーとしてのクエリエンティティインデックスを使用して記録が行われます。ループ内でローカルの Unity Mathematics `Random` を使用している場合、並列フィックスは元の乱数生成器の状態とそのインデックスから、エンティティごとにストリームのシード値を生成します。初期シードが同じであっても、ランダムな位置などの結果は元のループと異なる場合があります。ループ外でも状態が使用される RNG ローカル変数は対象外です。

サポートされていない構文には、その他のクエリフィルター、Enableable・Shared・Managed コンポーネント、任意のメソッド呼び出しや演算子、クエリループ自体を対象とする `break`、`return`、DynamicBuffer、各種 Lookup、`SystemBase`、および `[RequireMatchingQueriesForUpdate]` が含まれます。

## Diagnostics

* `EJ0001`: ループが変換可能であることを報告します。重要度「Info」でデフォルトで有効になっています。
* `EJ0002`: 認識されたクエリループにおいて、最初に見つかった未サポートの構文を報告します。デフォルトでは無効になっています。Suggestion として表示するには、`.editorconfig` に以下を追加してください。

```ini
[*.cs]
dotnet_diagnostic.EJ0002.severity = suggestion

```

## Rider の互換性

クイックフィックスは Rider に同梱されている Roslyn アセンブリに対してビルドされており、`EntitiesJobify/Editor/Analyzers/Rider` 配下に配置されています。Unity の Rider プロジェクトジェネレーターがこの DLL を生成された Rider プロジェクトに追加します。Unity コンパイラのアナライザーとしては登録されないため、Unity 側が Rider の新しい Roslyn アセンブリを読み込んだり、`CS9057` を報告したりすることはありません。互換性はホストとなる Rider のバージョンに依存します。パッケージのビルドに使用されたバージョン以外の Rider については、サポート対象として宣言されていません。

## パッケージの内容

| パス | 目的 |
| --- | --- |
| `EntitiesJobify/Editor/Analyzers/Entities.Jobify.Analyzers.dll` | `EJ0001` およびオプトインの `EJ0002` 用の Unity/Roslyn アナライザー |
| `EntitiesJobify/Editor/Analyzers/Rider/` | Rider 固有のコードフィックスとその Roslyn/MEF 依存関係 |
| `EntitiesJobify/Samples~/SimpleMovement/` | インポート可能な 2 コンポーネントの Entities サンプル |
| `EntitiesJobify/Samples~/ComplexMovement/` | 条件分岐、ベクトル演算、タグフィルターを含むインポート可能なサンプル |
| `Tools/` | このリポジトリ内のアナライザーとコードフィックスのソース、ビルドプロジェクト、および .NET テスト |
