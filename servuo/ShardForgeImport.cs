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
//   spawn   x y z <count> <homeRange> <minDelayMinutes> <maxDelayMinutes> <Creature:count,Creature:count,...>
//   region  x y w h <guarded 1|0> <name>
//   ore     <bankY> <one character per 8x8 bank: 0 iron .. 8 valorite, '.' none>
//
// Moongates: "item ... ShardForgeMoongate <1|2>" lines make ShardForgeMoongate items (below). Stepping into one
// opens a list of every other ShardForge moongate, named after the town (guarded region) it stands in. 1 marks a
// town players can start in: when there are any, new characters choose among them instead of the usual cities
// ([props a gate to change its StartCity or City name).
// Ore: the ore lines set which ore every bank of mountain gives (mined from the map: iron near towns, the rare
// ores deep in the mountains). They are set at every start; with them, veins no longer change at random when a
// bank refills (ServUO's RandomizeVeins is turned off, for every map).
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
using Server.Engines.Harvest;
using Server.Gumps;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Regions;
using Server.Spells;

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
            Timer.DelayCall(TimeSpan.Zero, new TimerCallback(LoadOre));
            // After the account handler's own (it subscribes earlier), so these cities are the ones sent.
            EventSink.GameLogin += new GameLoginEventHandler(OnGameLogin);
        }

        // New characters start in the towns with a ShardForge moongate marked as a starting city.
        private static void OnGameLogin(GameLoginEventArgs e)
        {
            if (!e.Accepted) return;
            List<CityInfo> cities = new List<CityInfo>();
            foreach (ShardForgeMoongate g in ShardForgeMoongate.All)
            {
                if (g.Deleted || !g.StartCity || g.Map == null || g.Map == Map.Internal) continue;
                cities.Add(new CityInfo(g.City, "Moongate", g.X, g.Y, g.Z, g.Map));
                if (cities.Count == 10) break; // the client's list has room for about this many
            }
            if (cities.Count > 0) e.CityInfo = cities.ToArray();
        }

        // Every bank of mountain gives the ore the map says (see "ore" above).
        private static void LoadOre()
        {
            HarvestDefinition def = Mining.System.OreAndStone;
            if (def.BankWidth != 8 || def.BankHeight != 8) { Console.WriteLine("ShardForge: ore banks aren't 8x8 here; the map's ore left out."); return; }
            int banks = 0;
            foreach (string file in Files())
            {
                Map map = Map.Felucca;
                foreach (string[] c in Lines(file))
                {
                    if (c[0] == "map") { map = Map.Parse(c[1]) ?? map; continue; }
                    if (c[0] != "ore" || c.Length < 3) continue;
                    Dictionary<Point2D, HarvestBank> byMap;
                    if (!def.Banks.TryGetValue(map, out byMap)) def.Banks[map] = byMap = new Dictionary<Point2D, HarvestBank>();
                    int by = I(c[1]);
                    for (int bx = 0; bx < c[2].Length; bx++)
                    {
                        int v = c[2][bx] - '0';
                        if (v < 0 || v >= def.Veins.Length) continue;
                        byMap[new Point2D(bx, by)] = new HarvestBank(def, def.Veins[v]);
                        banks++;
                    }
                }
            }
            if (banks == 0) return;
            def.RandomizeVeins = false;
            Console.WriteLine("ShardForge: ore set for {0} mountain banks.", banks);
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
                        if (c[0] == "region" || c[0] == "ore") continue;
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
            LoadOre();
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
                    if (c.Length > 8 && c[8] == "moongate")
                    {
                        // A moongate players can see and step into: the teleporter shows the gate art.
                        t.ItemID = 0xF6C; t.Visible = true;
                        if (c.Length > 9 && c[9].Length > 0) t.Name = c[9];
                        Set(t, "SourceEffect", true); Set(t, "DestEffect", true); Set(t, "SoundID", 0x20E);
                    }
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
                // "Orc:4": this many of that creature (plain "Orc" shares the total).
                string[] pair = raw.Trim().Split(':');
                string n = pair[0].Trim();
                if (n.Length == 0) continue;
                int each = pair.Length > 1 ? Math.Max(1, I(pair[1].Trim())) : Math.Max(1, count / Math.Max(1, names.Length));
                if (ScriptCompiler.FindTypeByName(n) == null) { Console.WriteLine("ShardForge: no creature class '{0}', left out of a spawner.", n); continue; }
                if (objects != null && so != null)
                {
                    IList list = (IList)objects.GetValue(sp, null);
                    ConstructorInfo ctor = so.GetConstructor(new Type[] { typeof(string), typeof(int) });
                    if (list != null && ctor != null) list.Add(ctor.Invoke(new object[] { n, each }));
                }
                else if (names1 != null)
                {
                    // Older spawners pick at random from the names: list each as often as its share.
                    IList list = (IList)names1.GetValue(sp, null);
                    if (list != null) for (int k = 0; k < Math.Min(each, 20); k++) list.Add(n);
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

    /// <summary>
    /// A moongate that lists every other ShardForge moongate and takes you to the one you pick, with your pets.
    /// Named after the town it stands in; StartCity makes the town one new characters can start in.
    /// </summary>
    public class ShardForgeMoongate : Item
    {
        public static readonly List<ShardForgeMoongate> All = new List<ShardForgeMoongate>();
        private static readonly Dictionary<Mobile, DateTime> m_Arrived = new Dictionary<Mobile, DateTime>();
        private string m_City;

        [CommandProperty(AccessLevel.GameMaster)]
        public string City
        {
            get
            {
                if (!string.IsNullOrEmpty(m_City)) return m_City;
                for (Region r = Map == null ? null : Region.Find(Location, Map); r != null && !r.IsDefault; r = r.Parent)
                    if (!string.IsNullOrEmpty(r.Name)) return r.Name;
                return "Moongate";
            }
            set { m_City = value; InvalidateProperties(); }
        }

        [CommandProperty(AccessLevel.Administrator)]
        public bool StartCity { get; set; }

        [Constructable]
        public ShardForgeMoongate() : this(1) { }

        /// <summary>1: a starting city; 2: not.</summary>
        [Constructable]
        public ShardForgeMoongate(int kind) : base(0xF6C)
        {
            Movable = false;
            Light = LightType.Circle300;
            StartCity = kind != 2;
            All.Add(this);
        }

        public ShardForgeMoongate(Serial serial) : base(serial) { All.Add(this); }

        public override string DefaultName { get { return "a moongate"; } }

        public override void OnAfterDelete() { base.OnAfterDelete(); All.Remove(this); }

        public override void GetProperties(ObjectPropertyList list)
        {
            base.GetProperties(list);
            list.Add(1060658, "{0}\t{1}", "Town", City); // ~1_val~: ~2_val~
        }

        public override bool OnMoveOver(Mobile m)
        {
            DateTime at;
            if (m.Player && !(m_Arrived.TryGetValue(m, out at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(3))) Use(m);
            return true;
        }

        public override void OnDoubleClick(Mobile m)
        {
            if (m.InRange(GetWorldLocation(), 1)) Use(m); else m.SendLocalizedMessage(500446); // That is too far away.
        }

        public bool CanUse(Mobile m)
        {
            if (m.Criminal) { m.SendLocalizedMessage(1005561, "", 0x22); return false; } // Thou'rt a criminal and cannot escape so easily.
            if (SpellHelper.CheckCombat(m)) { m.SendLocalizedMessage(1005564, "", 0x22); return false; } // Wouldst thou flee during the heat of battle??
            if (m.Spell != null) { m.SendLocalizedMessage(1049616); return false; } // You are too busy to do that at the moment.
            return true;
        }

        private void Use(Mobile m)
        {
            if (!CanUse(m)) return;
            m.CloseGump(typeof(ShardForgeMoongateGump));
            m.SendGump(new ShardForgeMoongateGump(this));
        }

        public void Travel(Mobile m, ShardForgeMoongate to)
        {
            if (to == null || to.Deleted || to.Map == null || to.Map == Map.Internal || Deleted || m.Map != Map || !m.InRange(GetWorldLocation(), 1) || !CanUse(m)) return;
            m_Arrived[m] = DateTime.UtcNow;
            BaseCreature.TeleportPets(m, to.Location, to.Map);
            m.MoveToWorld(to.Location, to.Map);
            Effects.PlaySound(to.Location, to.Map, 0x1FE);
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(0);
            writer.Write(m_City ?? "");
            writer.Write(StartCity);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            reader.ReadInt();
            m_City = reader.ReadString();
            StartCity = reader.ReadBool();
        }
    }

    public class ShardForgeMoongateGump : Gump
    {
        private readonly ShardForgeMoongate m_From;
        private readonly List<ShardForgeMoongate> m_To = new List<ShardForgeMoongate>();

        public ShardForgeMoongateGump(ShardForgeMoongate from) : base(100, 100)
        {
            m_From = from;
            foreach (ShardForgeMoongate g in ShardForgeMoongate.All)
                if (g != from && !g.Deleted && g.Map != null && g.Map != Map.Internal) m_To.Add(g);
            m_To.Sort(delegate (ShardForgeMoongate a, ShardForgeMoongate b)
            {
                int m = (a.Map == from.Map ? 0 : 1).CompareTo(b.Map == from.Map ? 0 : 1);
                return m != 0 ? m : string.Compare(a.City, b.City, StringComparison.OrdinalIgnoreCase);
            });
            int rows = Math.Max(1, m_To.Count), h = 70 + rows * 26;
            AddPage(0);
            AddBackground(0, 0, 300, h, 9270);
            AddLabel(20, 18, 1153, "Where to?");
            if (m_To.Count == 0) AddLabel(20, 46, 1150, "No other moongates yet.");
            for (int i = 0; i < m_To.Count; i++)
            {
                ShardForgeMoongate g = m_To[i];
                AddButton(20, 46 + i * 26, 4005, 4007, i + 1, GumpButtonType.Reply, 0);
                AddLabel(56, 48 + i * 26, 1150, g.Map == from.Map ? g.City : g.City + " (" + g.Map.Name + ")");
            }
        }

        public override void OnResponse(NetState state, RelayInfo info)
        {
            int i = info.ButtonID - 1;
            if (i >= 0 && i < m_To.Count) m_From.Travel(state.Mobile, m_To[i]);
        }
    }
}
