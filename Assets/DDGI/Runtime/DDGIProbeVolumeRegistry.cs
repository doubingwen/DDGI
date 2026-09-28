using System.Collections.Generic;

namespace Dou.DDGI
{
    public static class DDGIProbeVolumeRegistry
    {
        static readonly List<DDGIProbeVolume> Volumes = new List<DDGIProbeVolume>();

        public static DDGIProbeVolume PrimaryVolume
        {
            get
            {
                for (int index = Volumes.Count - 1; index >= 0; index--)
                {
                    DDGIProbeVolume volume = Volumes[index];
                    if (volume != null && volume.isActiveAndEnabled)
                        return volume;
                    Volumes.RemoveAt(index);
                }

                return null;
            }
        }

        internal static void Register(DDGIProbeVolume volume)
        {
            if (volume != null && !Volumes.Contains(volume))
                Volumes.Add(volume);
        }

        internal static void Unregister(DDGIProbeVolume volume)
        {
            Volumes.Remove(volume);
        }

        public static void GetActiveVolumesByDensity(List<DDGIProbeVolume> destination)
        {
            destination.Clear();
            for (int index = Volumes.Count - 1; index >= 0; index--)
            {
                DDGIProbeVolume volume = Volumes[index];
                if (volume == null)
                    Volumes.RemoveAt(index);
                else if (volume.isActiveAndEnabled)
                    destination.Add(volume);
            }
            destination.Sort(CompareDensity);
        }

        static int CompareDensity(DDGIProbeVolume left, DDGIProbeVolume right)
        {
            int densityOrder = right.WorldProbeDensity.CompareTo(left.WorldProbeDensity);
            return densityOrder != 0
                ? densityOrder
                : left.GetInstanceID().CompareTo(right.GetInstanceID());
        }
    }
}
