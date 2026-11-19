// ===========================================================================
// ShardForge import for ServUO: brings the doors, signs, teleporters, spawners, working items and regions placed
// in ShardForge Walk into your shard.
//
// Install: copy this file to Scripts/Custom/ShardForge/ and the .sfitems files to Data/ShardForge/, restart.
//   [SFImport   (Administrator) creates everything in Data/ShardForge/*.sfitems, replacing an earlier import.
//   [SFClear    (Administrator) deletes everything an earlier import created.
// Regions are registered at every start from the same files (they are not saved with the world).
//
// File format: tab-separated lines, '#' starts a comment.
//   map     <MapName>                                     the facet for the lines that follow
//   door    x y z <DoorType> <DoorFacing>
//   sign    x y z <itemID> <hue> <text>
//   item    x y z <TypeName> <itemID or 0> <hue>
//   tele    x y z <destX> <destY> <destZ> <destMap>
//   spawn   x y z <count> <homeRange> <minDelayMinutes> <maxDelayMinutes> <creature,creature,...>
//   region  x y w h <guarded 1|0> <name>
// Made by ShardForge (https://github.com/kakelakel/ShardForge). Change freely.
// ===========================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Server;
using Server.Commands;
using Server.Items;
using Server.Regions;

namespace Server.ShardForge
{
    public static class ShardForgeImport
    {
        private static string Folder { get { return Path.Combine(Core.BaseDirectory, Path.Combine("Data", "ShardForge")); } }
        private static string Ledger { get { return Path.Combine(Folder, "imported.txt"); } }
        private static readonly List<Region> m_Regions = new List<Region>();

        public static void Initialize()
        {
            CommandSystem.Register("SFImport", AccessLevel.Administrator, new CommandEventHandler(OnImport));
            CommandSystem.Register("SFClear", AccessLevel.Administrator, new CommandEventHandler(OnClear));
            Timer.DelayCall(TimeSpan.Zero, new TimerCallback(LoadRegions));
        }

        private static IEnumerable<string[]> Lines(string file)
        {
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                yield return line.Split('\t');
            }
        }

        private static string[] Files()
        {
            return Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.sfitems") : new string[0];
        }

        private static int I(string s) { return int.Parse(s.StartsWith("0x") ? s.Substring(2) : s, s.StartsWith("0x") ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture); }

        private static void LoadRegions()
        {
            foreach (Region r in m_Regions) r.Unregister();
            m_Regions.Clear();
            foreach (string file in Files())
            {
                Map map = Map.Felucca;
                foreach (string[] c in Lines(file))
                {
                    try
                    {
                        if (c[0] == "map") { map = Map.Parse(c[1]) ?? map; continue; }
                        if (c[0] != "region" || c.Length < 7) continue;
                        Rectangle3D[] area = Region.ConvertTo3D(new Rectangle2D[] { new Rectangle2D(I(c[1]), I(c[2]), I(c[3]), I(c[4])) });
                        Region region = c[5] == "1" ? new GuardedRegion(c[6], map, Region.DefaultPriority, area) : new Region(c[6], map, Region.DefaultPriority, area);
                        region.GoLocation = new Point3D(I(c[1]) + I(c[3]) / 2, I(c[2]) + I(c[4]) / 2, map.GetAverageZ(I(c[1]) + I(c[3]) / 2, I(c[2]) + I(c[4]) / 2));
                        region.Register();
                        m_Regions.Add(region);
                    }
                    catch (Exception e) { Console.WriteLine("ShardForge: skipped a region in {0}: {1}", Path.GetFileName(file), e.Message); }
                }
            }
            if (m_Regions.Count > 0) Console.WriteLine("ShardForge: {0} regions registered.", m_Regions.Count);
        }

        private static int Clear()
        {
            int n = 0;
            if (!File.Exists(Ledger)) return 0;
            foreach (string s in File.ReadAllLines(Ledger))
            {
                int serial;
                if (!int.TryParse(s, out serial)) continue;
                Item item = World.FindItem((Serial)serial);
                if (item != null && !item.Deleted) { item.Delete(); n++; }
            }
            File.Delete(Ledger);
            return n;
        }

        private static void OnClear(CommandEventArgs e)
        {
            e.Mobile.SendMessage("ShardForge: deleted {0} imported items.", Clear());
        }

        private static void OnImport(CommandEventArgs e)
        {
            int removed = Clear(), made = 0, failed = 0;
            List<string> ledger = new List<string>();
            foreach (string file in Files())
            {
                Map map = Map.Felucca;
                foreach (string[] c in Lines(file))
                {
                    try
                    {
                        if (c[0] == "map") { map = Map.Parse(c[1]) ?? map; continue; }
                        if (c[0] == "region") continue;
                        Item item = Make(c);
                        if (item == null) { failed++; continue; }
                        item.MoveToWorld(new Point3D(I(c[1]), I(c[2]), I(c[3])), map);
                        ledger.Add(item.Serial.Value.ToString());
                        made++;
                    }
                    catch (Exception ex) { failed++; Console.WriteLine("ShardForge: skipped '{0}': {1}", string.Join(" ", c), ex.Message); }
                }
            }
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(Ledger, ledger.ToArray());
            LoadRegions();
            e.Mobile.SendMessage("ShardForge: removed {0} old items, created {1}{2}, {3} regions.", removed, made, failed > 0 ? " (" + failed + " skipped, see the console)" : "", m_Regions.Count);
        }

        private static Item Make(string[] c)
        {
            switch (c[0])
            {
                case "door":
                {
                    Type t = ScriptCompiler.FindTypeByName(c[4]);
                    if (t == null || !typeof(BaseDoor).IsAssignableFrom(t)) return null;
                    DoorFacing facing = (DoorFacing)Enum.Parse(typeof(DoorFacing), c[5], true);
                    return (Item)Activator.CreateInstance(t, new object[] { facing });
                }
                case "sign":
                {
                    Static s = new Static(I(c[4]));
                    s.Hue = I(c[5]);
                    if (c.Length > 6 && c[6].Length > 0) s.Name = c[6];
                    return s;
                }
                case "item":
                {
                    Type t = ScriptCompiler.FindTypeByName(c[4]);
                    if (t == null || !typeof(Item).IsAssignableFrom(t)) return null;
                    int id = I(c[5]);
                    Item item = null;
                    if (id > 0 && t.GetConstructor(new Type[] { typeof(int) }) != null) item = (Item)Activator.CreateInstance(t, new object[] { id });
                    if (item == null) item = (Item)Activator.CreateInstance(t);
                    item.Movable = false;
                    if (I(c[6]) != 0) item.Hue = I(c[6]);
                    return item;
                }
                case "tele":
                {
                    Teleporter t = new Teleporter();
                    t.PointDest = new Point3D(I(c[4]), I(c[5]), I(c[6]));
                    t.MapDest = Map.Parse(c[7]);
                    return t;
                }
                case "spawn":
                    return MakeSpawner(I(c[4]), I(c[5]), I(c[6]), I(c[7]), c[8].Split(','));
            }
            return null;
        }

        // Spawners differ between ServUO versions (SpawnNames or SpawnObjects, Count or MaxCount): set what exists.
        private static Item MakeSpawner(int count, int range, int minDelay, int maxDelay, string[] names)
        {
            Type t = ScriptCompiler.FindTypeByName("Spawner");
            if (t == null) return null;
            Item sp = (Item)Activator.CreateInstance(t);
            Set(sp, "Count", count); Set(sp, "MaxCount", count);
            Set(sp, "HomeRange", range); Set(sp, "WalkingRange", range);
            Set(sp, "MinDelay", TimeSpan.FromMinutes(Math.Max(0, minDelay)));
            Set(sp, "MaxDelay", TimeSpan.FromMinutes(Math.Max(minDelay, maxDelay)));
            PropertyInfo names1 = t.GetProperty("SpawnNames"), objects = t.GetProperty("SpawnObjects");
            Type so = ScriptCompiler.FindTypeByName("SpawnObject");
            foreach (string raw in names)
            {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                if (objects != null && so != null)
                {
                    IList list = (IList)objects.GetValue(sp, null);
                    ConstructorInfo ctor = so.GetConstructor(new Type[] { typeof(string), typeof(int) });
                    if (list != null && ctor != null) list.Add(ctor.Invoke(new object[] { n, Math.Max(1, count / Math.Max(1, names.Length)) }));
                }
                else if (names1 != null)
                {
                    IList list = (IList)names1.GetValue(sp, null);
                    if (list != null) list.Add(n);
                }
            }
            Set(sp, "Running", true);
            MethodInfo respawn = t.GetMethod("Respawn", Type.EmptyTypes);
            if (respawn != null) Timer.DelayCall(TimeSpan.FromSeconds(1), delegate { if (!sp.Deleted) respawn.Invoke(sp, null); });
            return sp;
        }

        private static void Set(object o, string name, object value)
        {
            PropertyInfo p = o.GetType().GetProperty(name);
            if (p != null && p.CanWrite && p.PropertyType.IsAssignableFrom(value.GetType())) p.SetValue(o, value, null);
        }
    }
}
