using System.Collections.Generic;

namespace Dou.GI
{
    //启用Volume时 注册，不启用Volume时 注销
    //这样使用时就不用FindObjectOfType<RadianceFieldVolume>()  去每个模块中找Volume
    //直接使用RadianceFieldRegistry.PrimaryVolume 拿到方便快捷好管理
    internal static class RadianceFieldRegistry
    {   
        //Volume 链表，但是一般场景只有一个volume
        static readonly List<RadianceFieldVolume> ActiveVolumes = new List<RadianceFieldVolume>();

        internal static IReadOnlyList<RadianceFieldVolume> Volumes => ActiveVolumes;

        internal static RadianceFieldVolume PrimaryVolume
        {
            get
            {
                for (int index = 0; index < ActiveVolumes.Count; index++)
                {
                    RadianceFieldVolume volume = ActiveVolumes[index];
                    if (volume != null && volume.isActiveAndEnabled && volume.HasCoefficientHistory)
                        return volume;
                }

                return null;
            }
        }

        internal static void Register(RadianceFieldVolume volume)
        {
            if (volume != null && !ActiveVolumes.Contains(volume))
                ActiveVolumes.Add(volume);
        }

        internal static void Unregister(RadianceFieldVolume volume)
        {
            ActiveVolumes.Remove(volume);
        }
    }
}
