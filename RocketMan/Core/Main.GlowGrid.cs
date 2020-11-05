﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld;
using UnityEngine;
using Verse;
using static RocketMan.RocketShip;

namespace RocketMan
{
    public partial class Main
    {
        public static class GlowGrid_Patch
        {
            public static GlowerPorperties currentProp;

            public static Map[] maps = new Map[20];

            internal static bool deregister = false;
            internal static bool register = false;
            internal static bool calculating = false;

            public static HashSet<GlowerPorperties>[] removedProps = new HashSet<GlowerPorperties>[20];
            public static HashSet<GlowerPorperties>[] changedProps = new HashSet<GlowerPorperties>[20];

            public static Dictionary<CompGlower, GlowerPorperties> props = new Dictionary<CompGlower, GlowerPorperties>();

            public class GlowerPorperties
            {
                public CompGlower glower;

                public HashSet<int> indices;

                public Vector3 position = Vector3.zero;

                public bool beingUpdated = false;
                public bool drawen = false;

                public bool IsValid => !glower.parent.Destroyed && glower.ShouldBeLitNow;
                public bool ShouldRemove => glower.parent.Destroyed || !glower.ShouldBeLitNow;

                public GlowerPorperties(CompGlower glower)
                {
                    this.glower = glower;
                    this.indices = new HashSet<int>();
                    this.position = glower.parent.positionInt.ToVector3();
                    this.position.y = 0.0f;
                }

                public void Update()
                {
                    this.beingUpdated = true;
                    this.indices.Clear();
                }

                public void FinishUpdate()
                {
                    this.beingUpdated = false;
                }

                public bool Inersects(GlowerPorperties other)
                {
                    if (Vector3.Distance(other.position, this.position) + 1 < other.glower.Props.glowRadius + this.glower.Props.glowRadius)
                    {
                        return true;
                    }
                    return false;
                }

                public bool Contains(Vector3 loc)
                {
                    return Vector3.Distance(this.position, loc) + 1 < this.glower.Props.glowRadius;
                }

                public bool Contains(IntVec3 loc)
                {
                    return this.Contains(loc.ToVector3());
                }

                public void Reset()
                {
                    this.indices.Clear();
                    var dim = glower.Props.glowRadius * 2;
                    this.position = glower.parent.TrueCenter();
                    this.position.y = 0.0f;
                }

                public static GlowerPorperties GetGlowerPorperties([NotNull] CompGlower comp)
                {
                    if (comp == null)
                        return null;
                    if (props.TryGetValue(comp, out var prop))
                        return prop;
                    var result = new GlowerPorperties(comp);
                    props[comp] = result;
                    return result;
                }
            }

            [OnTickLong]
            public static void TickRefreshGrid()
            {
                if (!Finder.enableGridRefresh)
                    return;
#if DEBUG
                if (Finder.debug) Log.Message("ROCKETMAN: Refreshing all light grid");
#endif
                Finder.refreshGrid = true;
                Find.CurrentMap.glowGrid.RecalculateAllGlow();
            }

            private static bool TryRegisterMap(Map map)
            {
                var index = map.Index;
                if (index < 0 || index >= 20)
                    return false;
                if (maps[index] != map)
                {
                    maps[map.Index] = map;
                    removedProps[map.Index] = new HashSet<GlowerPorperties>();
                    changedProps[map.Index] = new HashSet<GlowerPorperties>();
                }
                return true;
            }

            [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.RegisterGlower))]
            internal static class RegisterGlower_Patch
            {
                internal static void Prefix(GlowGrid __instance, CompGlower newGlow)
                {
                    Map map = __instance.map;
                    register = true;
                    TryRegisterMap(map);

                    GlowerPorperties prop = GlowerPorperties.GetGlowerPorperties(newGlow);
                    if (props.ContainsKey(newGlow))
                    {
                        if (Finder.debug) Log.Warning(string.Format("ROCKETMAN: Double registering an registered glower {0}:{1}", newGlow, newGlow.parent));
                        return;
                    }
                    if (Finder.debug) Log.Warning(string.Format("ROCKETMAN: Registering an registered glower {0}:{1}", newGlow, newGlow.parent));
                }

                internal static void Postfix()
                {
                    register = false;
                }
            }

            [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.DeRegisterGlower))]
            internal static class DeRegisterGlower_Patch
            {
                internal static void Prefix(GlowGrid __instance, CompGlower oldGlow)
                {
                    Map map = __instance.map;
                    deregister = true;
                    TryRegisterMap(map);

                    GlowerPorperties prop;
                    if (Finder.debug) Log.Message(string.Format("ROCKETMAN: Removed {0}", oldGlow));
                    if (!props.ContainsKey(oldGlow))
                    {
                        if (Finder.debug && !removedProps[map.Index].Any(p => p.glower == oldGlow)) Log.Warning(string.Format("ROCKETMAN: Found an unregisterd {0}:{1}", oldGlow, oldGlow.parent));
                        return;
                    }
                    prop = props[oldGlow];

                    if (Finder.debug) Log.Message(string.Format("ROCKETMAN: Queued {0} for removal", oldGlow.parent));
                    removedProps[map.Index].Add(prop);
                }

                internal static void Postfix()
                {
                    deregister = false;
                }
            }


            [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.RecalculateAllGlow))]
            internal static class RecalculateAllGlow_Patch
            {
                internal static Color32[] tBufferedGrid;
                internal static Color32[] tEmptyGrid;

                internal static void Prefix(GlowGrid __instance)
                {
                    var map = __instance.map;
                    if (!TryRegisterMap(map))
                        return;
                    if (Finder.refreshGrid)
                    {
                        tEmptyGrid.CopyTo(__instance.glowGrid, 0);
                        tEmptyGrid.CopyTo(__instance.glowGridNoCavePlants, 0);
                        return;
                    }
                    if (tEmptyGrid == null || tEmptyGrid.Length != __instance.glowGrid.Length)
                    {
                        tEmptyGrid = new Color32[__instance.glowGrid.Length];
                        tBufferedGrid = new Color32[__instance.glowGrid.Length];
                        for (int i = 0; i < __instance.glowGrid.Length; i++)
                        {
                            tEmptyGrid[i] = new Color32(0, 0, 0, 0);
                        }
                    }
                    tEmptyGrid.CopyTo(__instance.glowGridNoCavePlants, 0);
                    calculating = true;
                }

                internal static void Postfix(GlowGrid __instance)
                {
                    var map = __instance.map;
                    var mapIndex = map.Index;

                    if (Finder.refreshGrid)
                    {
                        Finder.refreshGrid = false;
                        return;
                    }

                    if (Finder.debug) Log.Message(string.Format("ROCKETMAN: Recalculationg for removed with {0} queued for removal", removedProps[mapIndex].Count));
                    foreach (var prop in removedProps[mapIndex])
                    {
                        tEmptyGrid.CopyTo(tBufferedGrid, 0);
                        FixRemovedGlowers(__instance, prop);
                        props.Remove(prop.glower);
                    }

                    if (Finder.debug) Log.Message(string.Format("ROCKETMAN: Recalculationg for changes with {0} queued for changes", changedProps[mapIndex].Count));
                    if (changedProps[mapIndex].Count != 0)
                    {
                        tEmptyGrid.CopyTo(tBufferedGrid, 0);
                        FixChanged(__instance);
                    }

                    calculating = false;
                    FinalizeNewGlowers(__instance);
                    FinalizeCleanUp(__instance);
                }

                internal static void FloodGlow(GlowerPorperties prop, Color32[] grid, Map map, GlowFlooder flooder)
                {
                    if (removedProps[map.Index].Contains(prop))
                    {
                        return;
                    }
                    if (prop.glower.parent.Destroyed || !prop.glower.parent.Spawned)
                    {
                        return;
                    }
                    flooder.AddFloodGlowFor(prop.glower, grid);
                }

                internal static void AddFloodGlowFor(CompGlower glower, Color32[] glowGrid)
                {
                    GlowerPorperties.GetGlowerPorperties(glower);
                    if (Finder.refreshGrid)
                    {
                        var flooder = glower.parent.Map.glowFlooder;
                        flooder.AddFloodGlowFor(glower, tBufferedGrid);
                    }
                }

                private static void FixChanged(GlowGrid instance)
                {
                    var map = instance.map;
                    var mapIndex = map.Index;

                    var queue = new Queue<GlowerPorperties>();
                    foreach (var glower in instance.litGlowers)
                    {
                        var prop = GlowerPorperties.GetGlowerPorperties(glower);
                        queue.Enqueue(prop);
                    }
                    while (true)
                    {
                        var count = changedProps[mapIndex].Count;
                        var tqueue = new Queue<GlowerPorperties>();
                        var changedArray = changedProps[mapIndex].ToArray();
                        while (queue.Count > 0)
                        {
                            var prop = queue.Dequeue();
                            if (!prop.drawen || changedProps[mapIndex].Contains(prop))
                                continue;
                            var found = false;
                            foreach (var other in changedArray)
                                if (other != prop && other.Inersects(prop))
                                {
                                    changedProps[mapIndex].Add(prop);
                                    found = true;
                                    break;
                                }
                            if (!found) tqueue.Enqueue(prop);
                        }
                        if (count == changedProps[mapIndex].Count)
                            break;
                        queue = tqueue;
                    }
                    foreach (var prop in changedProps[mapIndex])
                        foreach (var index in prop.indices)
                        {
                            instance.glowGrid[index] = new Color32(0, 0, 0, 0);
#if DEBUG
                            if (Finder.drawGlowerUpdates) map.debugDrawer.FlashCell(map.cellIndices.IndexToCell(index), 0.6f, "000", 10);
#endif
                        }
                    foreach (var prop in changedProps[mapIndex])
                        FloodGlow(prop, tBufferedGrid, map, map.glowFlooder);
                    foreach (var prop in changedProps[mapIndex])
                        foreach (var index in prop.indices)
                        {
                            instance.glowGrid[index] = tBufferedGrid[index];
#if DEBUG
                            if (Finder.drawGlowerUpdates) map.debugDrawer.FlashCell(map.cellIndices.IndexToCell(index), 0.6f, "1__", 50);
#endif
                        }
                }

                private static void FixRemovedGlowers(GlowGrid instance, GlowerPorperties prop)
                {
                    if (prop.glower?.parent?.Destroyed ?? true)
                    {
                        calculating = false;
                        instance.MarkGlowGridDirty(prop.position.ToIntVec3());
                        calculating = true;
                    }
                    var flooder = instance.map.glowFlooder;
                    var mapIndex = instance.map.Index;
                    foreach (var otherGlower in instance.litGlowers)
                    {
                        var other = GlowerPorperties.GetGlowerPorperties(otherGlower);
                        if (other != prop && other.drawen && other.Inersects(prop))
                        {
                            FloodGlow(other, tBufferedGrid, instance.map, flooder);
                        }
                    }
                    foreach (var index in prop.indices)
                    {
                        instance.glowGrid[index] = tBufferedGrid[index];
#if DEBUG
                        if (Finder.drawGlowerUpdates) instance.map.debugDrawer.FlashCell(
                            instance.map.cellIndices.IndexToCell(index), 0.05f, "_1_", 50);
#endif
                    }
                }

                private static void FinalizeNewGlowers(GlowGrid instance)
                {
                    var map = instance.map;
                    var flooder = map.glowFlooder;
                    foreach (var glower in instance.litGlowers)
                    {
                        var prop = GlowerPorperties.GetGlowerPorperties(glower);
                        if (!prop.drawen)
                        {
                            flooder.AddFloodGlowFor(prop.glower, instance.glowGrid);
                        }
                    }
                }

                private static void FinalizeCleanUp(GlowGrid instance)
                {
                    var map = instance.map;
                    var mapIndex = map.Index;
                    removedProps[mapIndex].Clear();
                    changedProps[mapIndex].Clear();
                }

                private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
                {
                    var codes = instructions.ToList();
                    var finished = false;
                    var mNumGridCells = AccessTools.PropertyGetter(typeof(CellIndices), nameof(CellIndices.NumGridCells));
                    var mAddFloodGlowFor = AccessTools.Method(typeof(GlowFlooder), nameof(GlowFlooder.AddFloodGlowFor));
                    var fMap = AccessTools.Field(typeof(GlowGrid), nameof(GlowGrid.map));
                    var fGlowFlooder = AccessTools.Field(typeof(Map), nameof(Map.glowFlooder));
                    for (int i = 0; i < codes.Count; i++)
                    {
                        CodeInstruction code = codes[i];
                        if (!finished)
                        {
                            if (code.Calls(mNumGridCells))
                            {
                                yield return codes[i];
                                yield return new CodeInstruction(OpCodes.Pop);
                                yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                                continue;
                            }

                            if (i + 2 < codes.Count
                                && codes[i].opcode == OpCodes.Ldarg_0
                                && codes[i + 1].opcode == OpCodes.Ldfld
                                && codes[i + 1].OperandIs(fMap)
                                && codes[i + 2].opcode == OpCodes.Ldfld
                                && codes[i + 2].OperandIs(fGlowFlooder))
                            {
                                i += 2;
                                continue;
                            }

                            if (code.Calls(mAddFloodGlowFor))
                            {
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RecalculateAllGlow_Patch), nameof(RecalculateAllGlow_Patch.AddFloodGlowFor)));
                                finished = true;
                                continue;
                            }
                        }
                        yield return code;
                    }
                }
            }

            [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.MarkGlowGridDirty))]
            internal static class MarkGlowGridDirty_Patch
            {
                public static void Prefix(GlowGrid __instance, IntVec3 loc)
                {
                    if (Current.ProgramState == ProgramState.Playing)
                    {
                        var map = __instance.map;
                        var mapIndex = map.Index;
                        if (TryRegisterMap(map))
                        {
                            if (!loc.IsValid || !loc.InBounds(map))
                            {
                                return;
                            }
                            if (register || calculating || deregister)
                            {
                                return;
                            }
#if DEBUG
                            if (Finder.debug) Log.Message(string.Format("ROCKETMAN: Map glow grid dirty at {0}", loc));
#endif
                            var changedPos = loc.ToVector3();
                            foreach (var glower in __instance.litGlowers)
                            {
                                var prop = GlowerPorperties.GetGlowerPorperties(glower);
                                if (prop.Contains(changedPos))
                                {
                                    changedProps[mapIndex].Add(prop);
#if DEBUG
                                    if (Finder.debug) Log.Message(string.Format("ROCKETMAN: Changed and glow grid dirty at {0} for {1}", loc, glower.parent));
#endif
                                }
                            }
                        }
                    }
                }
            }

            [HarmonyPatch(typeof(GlowFlooder), nameof(GlowFlooder.AddFloodGlowFor))]
            internal static class AddFloodGlow_Patch
            {
                internal static void Prefix(CompGlower theGlower)
                {
                    currentProp = GlowerPorperties.GetGlowerPorperties(theGlower);
                    currentProp.Update();
                }

                internal static void Postfix(CompGlower theGlower)
                {
                    if (currentProp == null && theGlower == null)
                    {
                        Log.Warning("ROCKETMAN: AddFloodGlow_Patch with null currentProp");
                        return;
                    }
                    if (currentProp == null)
                    {
                        currentProp = GlowerPorperties.GetGlowerPorperties(theGlower);
                        if (currentProp == null)
                            throw new InvalidDataException("ROCKETMAN: AddFloodGlow_Patch with null currentProp");
                    }
                    currentProp.FinishUpdate();
                    currentProp.drawen = true;
                    currentProp = null;
                }
            }

            [HarmonyPatch(typeof(GlowFlooder), nameof(GlowFlooder.SetGlowGridFromDist))]
            internal static class SetGlowGridFromDist_Patch
            {
                internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
                {
                    var codes = instructions.ToList();

                    for (int i = 0; i < codes.Count - 1; i++)
                        yield return codes[i];

                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Ldloc_1);
                    yield return new CodeInstruction(OpCodes.Ldloc_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SetGlowGridFromDist_Patch), nameof(SetGlowGridFromDist_Patch.PushIndex)));
                    yield return codes.Last();
                }

                internal static void PushIndex(int index, ColorInt color, float distance)
                {
                    currentProp.indices.Add(index);
#if DEBUG
                    if (Finder.debug && Finder.drawGlowerUpdates)
                    {
                        Map map = Find.CurrentMap;
                        IntVec3 cell = map.cellIndices.IndexToCell(index);
                        map.debugDrawer.FlashCell(cell, colorPct: 0.1f, duration: 100, text: "a");
                    }
#endif
                }
            }
        }
    }
}
