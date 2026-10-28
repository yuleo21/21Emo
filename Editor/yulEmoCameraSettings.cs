#if UNITY_EDITOR
using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace yulEmo
{
    /// <summary>
    /// アイコン撮影のカメラ位置の設定。プレビューウィンドウと撮影の両方が同じ値を使う。
    ///
    /// 位置はすべて「標準の位置」からのずれで、単位はメートル。
    ///   Right   : カメラから見て右へ動かす量（+ で右）
    ///   Up      : 上へ動かす量（+ で上）
    ///   Forward : 顔へ近づく量（+ で近づく、- で離れる）
    ///   Fov     : 画角（度）。小さいほど拡大される
    /// カメラの向きは常にアバターの正面を向いたままで、位置だけが平行移動する。
    /// 値は EditorPrefs に保存され、Unity を再起動しても残る。
    /// </summary>
    [Serializable]
    public sealed class yulEmoCameraSettings
    {
        public const float DefaultFov = 20f;
        public const float MinFov = 5f;
        public const float MaxFov = 60f;
        public const float MaxPan = 0.5f;
        public const float MaxDolly = 1f;

        private const string PrefsKey = "yulEmo.CameraSettings";

        public float Right;
        public float Up;
        public float Forward;
        public float Fov = DefaultFov;

        private static yulEmoCameraSettings _current;

        /// <summary>現在の設定。初回アクセス時に EditorPrefs から読み込む。</summary>
        public static yulEmoCameraSettings Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
        }

        public bool IsDefault =>
            Mathf.Approximately(Right, 0f) &&
            Mathf.Approximately(Up, 0f) &&
            Mathf.Approximately(Forward, 0f) &&
            Mathf.Approximately(Fov, DefaultFov);

        public void ResetToDefault()
        {
            Right = 0f;
            Up = 0f;
            Forward = 0f;
            Fov = DefaultFov;
        }

        /// <summary>範囲外の値を許容範囲に収める。</summary>
        public void Clamp()
        {
            Right = Mathf.Clamp(Right, -MaxPan, MaxPan);
            Up = Mathf.Clamp(Up, -MaxPan, MaxPan);
            Forward = Mathf.Clamp(Forward, -MaxDolly, MaxDolly);
            Fov = Mathf.Clamp(Fov, MinFov, MaxFov);
        }

        public static void Save()
        {
            if (_current == null) return;
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(_current));
        }

        private static yulEmoCameraSettings Load()
        {
            var json = EditorPrefs.GetString(PrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var loaded = JsonUtility.FromJson<yulEmoCameraSettings>(json);
                    if (loaded != null)
                    {
                        loaded.Clamp();
                        return loaded;
                    }
                }
                catch (Exception)
                {
                    // 壊れた保存値は無視して標準に戻す
                }
            }
            return new yulEmoCameraSettings();
        }

        public override string ToString()
        {
            var c = CultureInfo.InvariantCulture;
            return $"左右 {Right.ToString("0.000", c)} / 上下 {Up.ToString("0.000", c)} / 前後 {Forward.ToString("0.000", c)} / 画角 {Fov.ToString("0.0", c)}";
        }
    }
}
#endif
