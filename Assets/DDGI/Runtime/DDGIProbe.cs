using UnityEngine;

namespace Dou.DDGI
{
    public enum DDGIProbeState
    {
        Off = 0,
        Vigilant = 1,
        Uninitialized = 2,
        Sleep = 3,
        Awake = 4,
        NewAwake = 5,
        NewVigilant = 6
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class DDGIProbe : MonoBehaviour
    {
        public const int AtlasBorderSize = 1;
        public const int IrradianceResolution = 6;
        public const int DistanceResolution = 14;
        public const int IrradianceTileSize = IrradianceResolution + AtlasBorderSize * 2;
        public const int DistanceTileSize = DistanceResolution + AtlasBorderSize * 2;

        [SerializeField, HideInInspector] int linearIndex = -1;
        [SerializeField, HideInInspector] Vector3Int gridCoordinate;
        [SerializeField, HideInInspector] Vector2Int atlasTileCoordinate;
        [SerializeField, HideInInspector] DDGIProbeState state = DDGIProbeState.Uninitialized;

        public int LinearIndex => linearIndex;  // LinearIndex  会返回linerIndex
        public Vector3Int GridCoordinate => gridCoordinate;
        public Vector2Int AtlasTileCoordinate => atlasTileCoordinate;
        public Vector3 Position => transform.position;
        public DDGIProbeState State => state;
        public bool IsActive => state == DDGIProbeState.Vigilant || state == DDGIProbeState.Awake ||
                                state == DDGIProbeState.NewAwake || state == DDGIProbeState.NewVigilant;

        //probe纹理起点
        public Vector2Int IrradianceTileOrigin => atlasTileCoordinate * IrradianceTileSize;
        public Vector2Int DistanceTileOrigin => atlasTileCoordinate * DistanceTileSize;
        //probe纹理起点有效位置，跳过边界
        public Vector2Int IrradianceInteriorOrigin => IrradianceTileOrigin + Vector2Int.one * AtlasBorderSize;
        public Vector2Int DistanceInteriorOrigin => DistanceTileOrigin + Vector2Int.one * AtlasBorderSize;

        public void Configure(int probeIndex, Vector3Int coordinate, int atlasTilesPerRow)
        {
            if (probeIndex < 0)
                throw new System.ArgumentOutOfRangeException(nameof(probeIndex));
            if (atlasTilesPerRow <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(atlasTilesPerRow));

            linearIndex = probeIndex;
            gridCoordinate = coordinate;
            atlasTileCoordinate = ToAtlasTileCoordinate(probeIndex, atlasTilesPerRow);
        }

        public void SetActive(bool active)
        {
            state = active ? DDGIProbeState.Vigilant : DDGIProbeState.Off;
        }

        internal void SetRuntimeState(DDGIProbeState runtimeState) => state = runtimeState;
        //获得当前 Probe 在对应 Atlas 中的有效采样范围
        public Rect GetIrradianceInteriorUvRect(Vector2Int atlasResolution)
        {
            return GetInteriorUvRect(
                IrradianceInteriorOrigin,
                IrradianceResolution,
                atlasResolution);
        }

        public Rect GetDistanceInteriorUvRect(Vector2Int atlasResolution)
        {
            return GetInteriorUvRect(
                DistanceInteriorOrigin,
                DistanceResolution,
                atlasResolution);
        }

        public static Vector2Int ToAtlasTileCoordinate(int probeIndex, int atlasTilesPerRow)
        {
            return new Vector2Int(
                probeIndex % atlasTilesPerRow,
                probeIndex / atlasTilesPerRow);
        }

        //映射到0-1之间
        static Rect GetInteriorUvRect(
            Vector2Int interiorOrigin,
            int interiorResolution,
            Vector2Int atlasResolution)
        {
            if (atlasResolution.x <= 0 || atlasResolution.y <= 0)
                return default;

            Vector2 minimum = new Vector2(
                (interiorOrigin.x + 0.5f) / atlasResolution.x,
                (interiorOrigin.y + 0.5f) / atlasResolution.y);
            Vector2 maximum = new Vector2(
                (interiorOrigin.x + interiorResolution - 0.5f) / atlasResolution.x,
                (interiorOrigin.y + interiorResolution - 0.5f) / atlasResolution.y);
            return Rect.MinMaxRect(minimum.x, minimum.y, maximum.x, maximum.y);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = state switch
            {
                DDGIProbeState.Off => Color.gray,
                DDGIProbeState.Sleep => new Color(0.65f, 0.55f, 0.1f),
                DDGIProbeState.Awake => new Color(1.0f, 0.65f, 0.1f),
                DDGIProbeState.NewAwake => Color.magenta,
                DDGIProbeState.NewVigilant => Color.red,
                DDGIProbeState.Vigilant => Color.green,
                _ => Color.white
            };
            Gizmos.DrawWireSphere(Position, 0.12f);
        }
    }
}
