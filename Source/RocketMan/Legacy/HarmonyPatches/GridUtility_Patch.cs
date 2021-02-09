using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RocketMan.src.HarmonyPatches
{
    [StaticConstructorOnStartup]
    public static class GridUtility_Patch
    {
        private static Dictionary<GridTempModel, CacheableTick<float>> _cache 
            = new Dictionary<GridTempModel, CacheableTick<float>>(GridTempModelComparer.Instance);

        private static MethodInfo _getTempOriginal = 
            typeof(GridsUtility).GetMethod(nameof(GridsUtility.GetTemperature), BindingFlags.Static | BindingFlags.Public);

        private static MethodInfo _getTempPrefix =
            typeof(GridUtility_Patch).GetMethod(nameof(GetTempPrefix), BindingFlags.Static | BindingFlags.Public);

        private static MethodInfo _getTempPostfix =
            typeof(GridUtility_Patch).GetMethod(nameof(GetTempPostfix), BindingFlags.Static | BindingFlags.Public);

        static GridUtility_Patch()
        {
            HarmonyUtility.Instance.Patch(_getTempOriginal, new HarmonyMethod(_getTempPrefix), new HarmonyMethod(_getTempPostfix));
        }

        public static bool GetTempPrefix(ref float __result, IntVec3 loc, Map map)
        {
            if (_cache.TryGetValue(new GridTempModel(loc, map), out CacheableTick<float> value)
                && !value.ShouldUpdate(out _))
            {
                __result = value;
                return false;
            }

            return true;
        }

        public static void GetTempPostfix(ref float __result, IntVec3 loc, Map map)
        {
            if (Current.Game != null)
            {
                GridTempModel model = new GridTempModel(loc, map);
                if (_cache.TryGetValue(model, out CacheableTick<float> value))
                {
                    value.Value = __result;
                }
                else
                {
                    _cache[model] = MakeCache(__result);
                }
            }
        }

        private static CacheableTick<float> MakeCache(float initValue)
        {
            return new CacheableTick<float>(
                initValue
                , () => Find.TickManager.TicksGame
                , 2
                , null
                , Find.TickManager.TicksGame);
        }
    }
}
