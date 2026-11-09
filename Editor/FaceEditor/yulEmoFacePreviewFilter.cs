#if UNITY_EDITOR
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// 表情作成のプレビューで、シェイプキーの値を「プレビュー用のコピー（プロキシ）」だけに設定する NDMF Preview のフィルター。
    ///
    /// 本物のアバターのシェイプキーは変えない。NDMF Preview は、本物のレンダラーの代わりにコピーを描画する。
    /// このフィルターは、そのコピーに、毎フレーム値を設定する。
    /// コピーは NDMF の他のフィルター（Modular Avatar など）を通った後のものなので、衣装などが反映された状態の表情を確認できる。
    /// </summary>
    internal sealed class yulEmoFacePreviewFilter : IRenderFilter
    {
        /// <summary>プレビューに反映する値。本物のレンダラーごとに、シェイプキー名と値（0〜100）の並び。</summary>
        internal sealed class State
        {
            // 参照ごと入れ替えて更新する（OnFrame は毎フレーム、この参照を読むだけ）
            public Dictionary<Renderer, KeyValuePair<string, float>[]> Weights =
                new Dictionary<Renderer, KeyValuePair<string, float>[]>();
        }

        private readonly ImmutableList<RenderGroup> _groups;
        private readonly State _state;

        public yulEmoFacePreviewFilter(IEnumerable<SkinnedMeshRenderer> renderers, State state)
        {
            var builder = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var renderer in renderers)
            {
                if (renderer != null) builder.Add(RenderGroup.For(renderer));
            }
            _groups = builder.ToImmutable();
            _state = state;
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            return _groups;
        }

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            return Task.FromResult<IRenderFilterNode>(new Node(_state));
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly State _state;

            public Node(State state)
            {
                _state = state;
            }

            public RenderAspects WhatChanged => RenderAspects.Shapes;

            // このノードは、コピーのメッシュなどに依存した状態を持たないので、そのまま使い続ける
            public Task<IRenderFilterNode> Refresh(
                IEnumerable<(Renderer, Renderer)> proxyPairs,
                ComputeContext context,
                RenderAspects updatedAspects)
            {
                return Task.FromResult<IRenderFilterNode>(this);
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (original == null || proxy == null) return;
                if (!_state.Weights.TryGetValue(original, out var values)) return;

                var smr = proxy as SkinnedMeshRenderer;
                if (smr == null || smr.sharedMesh == null) return;

                // 他のフィルター（メッシュの統合など）でシェイプキーの並びが変わっていても動くように、名前で探す
                foreach (var pair in values)
                {
                    var index = smr.sharedMesh.GetBlendShapeIndex(pair.Key);
                    if (index >= 0) smr.SetBlendShapeWeight(index, pair.Value);
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
#endif
