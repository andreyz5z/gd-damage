// Grim Dawn player.gdc — reader / writer.
//
// The file is a stream of primitives encrypted with a self-keying XOR cipher,
// framed into id/length blocks. See README.md for the format description.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc
{
    // ---------------------------------------------------------------- cipher

    // Builds the 256-entry XOR table both the reader and the writer key off.
    public class Crypt
    {
        public uint Key;
        public readonly uint[] Table = new uint[256];

        public Crypt(uint seed)
        {
            uint k = seed;
            Key = k;
            for (int i = 0; i < 256; i++)
            {
                k = (k >> 1) | (k << 31);
                k = k * 39916801u;
                Table[i] = k;
            }
        }

        public void Advance(byte raw) { Key ^= Table[raw]; }
    }

    public class Reader
    {
        readonly byte[] d;
        readonly Crypt c;
        int pos;

        public Reader(byte[] data)
        {
            d = data;
            uint seed = BitConverter.ToUInt32(d, 0) ^ 0x55555555u;
            c = new Crypt(seed);
            pos = 4;
        }

        public uint Seed { get { return BitConverter.ToUInt32(d, 0) ^ 0x55555555u; } }
        public int Pos { get { return pos; } }
        public int Length { get { return d.Length; } }
        public bool AtEnd { get { return pos >= d.Length; } }

        public byte U8()
        {
            byte raw = d[pos++];
            byte v = (byte)(raw ^ (byte)(c.Key & 0xFF));
            c.Advance(raw);
            return v;
        }

        // advanceKey=false is used for block lengths and block terminators:
        // those four bytes are consumed but must not feed the key.
        public uint U32(bool advanceKey)
        {
            uint raw = BitConverter.ToUInt32(d, pos);
            uint v = raw ^ c.Key;
            if (advanceKey) for (int i = 0; i < 4; i++) c.Advance(d[pos + i]);
            pos += 4;
            return v;
        }

        public uint U32() { return U32(true); }
        public int I32() { return (int)U32(true); }
        public float F32() { return BitConverter.ToSingle(BitConverter.GetBytes(U32(true)), 0); }

        public byte[] Bytes(int n)
        {
            byte[] r = new byte[n];
            for (int i = 0; i < n; i++) r[i] = U8();
            return r;
        }

        public string Str()
        {
            uint n = U32();
            if (n > 0x100000) throw new GdcException("implausible string length " + n + " at offset " + (pos - 4));
            StringBuilder sb = new StringBuilder((int)n);
            for (uint i = 0; i < n; i++) sb.Append((char)U8());
            return sb.ToString();
        }

        public string WStr()
        {
            uint n = U32();
            if (n > 0x100000) throw new GdcException("implausible wide string length " + n + " at offset " + (pos - 4));
            StringBuilder sb = new StringBuilder((int)n);
            for (uint i = 0; i < n; i++) { int lo = U8(); int hi = U8(); sb.Append((char)(lo | (hi << 8))); }
            return sb.ToString();
        }

        // The whole decryption state is one position plus one key word, so a
        // speculative parse can be rewound and retried with a different layout.
        public class State { public int Pos; public uint Key; }

        public State Mark()
        {
            State s = new State();
            s.Pos = pos; s.Key = c.Key;
            return s;
        }

        public void Restore(State s) { pos = s.Pos; c.Key = s.Key; }

        public uint BlockStart(out int payloadEnd)
        {
            uint id = U32(true);
            uint len = U32(false);
            payloadEnd = pos + (int)len;
            if (payloadEnd < pos || payloadEnd > d.Length)
                throw new GdcException("block " + id + " at offset " + (pos - 8) + " declares length " + len);
            return id;
        }

        public void BlockEnd(int payloadEnd, uint id)
        {
            if (pos != payloadEnd)
                throw new GdcException("block " + id + ": parser stopped at " + pos + ", payload ends at " + payloadEnd
                                       + " (off by " + (pos - payloadEnd) + ")");
            uint term = U32(false);
            if (term != 0)
                throw new GdcException("block " + id + ": terminator is 0x" + term.ToString("X8") + ", expected 0");
        }
    }

    public class Writer
    {
        readonly List<byte> buf = new List<byte>();
        readonly Crypt c;

        public Writer(uint seed)
        {
            c = new Crypt(seed);
            buf.AddRange(BitConverter.GetBytes(seed ^ 0x55555555u));
        }

        public int Pos { get { return buf.Count; } }
        public byte[] ToArray() { return buf.ToArray(); }

        public void U8(byte v)
        {
            byte raw = (byte)(v ^ (byte)(c.Key & 0xFF));
            buf.Add(raw);
            c.Advance(raw);
        }

        public void U32(uint v, bool advanceKey)
        {
            uint raw = v ^ c.Key;
            byte[] b = BitConverter.GetBytes(raw);
            buf.AddRange(b);
            if (!advanceKey) return;
            for (int i = 0; i < 4; i++) c.Advance(b[i]);
        }

        public void U32(uint v) { U32(v, true); }
        public void I32(int v) { U32((uint)v, true); }
        public void F32(float v) { U32(BitConverter.ToUInt32(BitConverter.GetBytes(v), 0), true); }
        public void Bytes(byte[] v) { for (int i = 0; i < v.Length; i++) U8(v[i]); }

        public void Str(string s)
        {
            if (s == null) s = "";
            U32((uint)s.Length);
            for (int i = 0; i < s.Length; i++) U8((byte)s[i]);
        }

        public void WStr(string s)
        {
            if (s == null) s = "";
            U32((uint)s.Length);
            for (int i = 0; i < s.Length; i++) { U8((byte)(s[i] & 0xFF)); U8((byte)(s[i] >> 8)); }
        }

        // The length is only known once the payload is written, but because the
        // length field never feeds the key we can patch it in afterwards.
        public class Block { public int LenOffset; public uint KeyAtLen; }

        public Block BlockStart(uint id)
        {
            U32(id, true);
            Block b = new Block();
            b.LenOffset = buf.Count;
            b.KeyAtLen = c.Key;
            buf.AddRange(new byte[4]);
            return b;
        }

        public void BlockEnd(Block b)
        {
            uint len = (uint)(buf.Count - (b.LenOffset + 4));
            byte[] raw = BitConverter.GetBytes(len ^ b.KeyAtLen);
            for (int i = 0; i < 4; i++) buf[b.LenOffset + i] = raw[i];
            U32(0, false);
        }
    }

    public class GdcException : Exception
    {
        public GdcException(string m) : base(m) { }
    }

    // ----------------------------------------------------------------- model

    public class Item
    {
        public string BaseName = "", PrefixName = "", SuffixName = "", ModifierName = "", TransmuteName = "";
        public uint Seed;
        // NOT A FIELD OF THE SAVE — never read from disk and never written there. It marks gear
        // that a STAND put on the character itself (`ExactRolls.Worn.AsItem`): such an item names
        // only a record, it never had a seed, and its numbers come from the stand's table.
        //
        // A REAL worn item whose seed is zero is a different thing entirely, and until 22 October
        // the two were told apart by `Seed == 0`, which cannot tell them apart at all. The game
        // rolls a zero seed too, and the roll lands at the BOTTOM of every bracket — the
        // generator (`ItemRoll.Next`) has a fixed point at zero, so every word of the stream is
        // zero and every field takes `low + 0`. Analysis and the ten-field measurement that
        // settled it — MECHANICS 172.
        public bool FromStand;
        public string RelicName = "", RelicBonus = "";
        public uint RelicSeed;
        public string AugmentName = "";
        public uint Unknown, AugmentSeed;
        // Ascension (Astarkarn, August 2026): a worn item can be ascended, and
        // the affix that grants it is stored here — a LootRandomizer record under
        // records/items/lootaffixes/ascended/. It sits right after augmentSeed and
        // takes the room of the first two trailing words, so a save with nothing
        // ascended is byte-identical either way: the empty string writes the same
        // four zero bytes the old layout had there.
        public string AscensionName = "";
        public uint AscensionSeed;
        public bool HasAscension = true;   // whether this save's layout carries the field
        // Trailing 32-bit fields whose meaning is only partly known. Older saves
        // (inventory version 4) have two of them, saves before ascension six, and
        // current ones four. One of them is the stack count, verified against
        // component and potion stacks: index 3 when there are six words, and index 1
        // otherwise — the ascension pair ate the two words in front of it.
        public uint[] Extra = new uint[4];
        public uint StackCount
        {
            get { return Extra[StackIndex]; }
            set { Extra[StackIndex] = value; }
        }
        int StackIndex { get { return Extra.Length == 6 ? 3 : 1; } }

        // Grid position. Inventory bags store plain ints, the stash stores floats;
        // both are four bytes so the raw value is kept and reinterpreted on demand.
        public uint XRaw, YRaw;
        public float XFloat { get { return BitConverter.ToSingle(BitConverter.GetBytes(XRaw), 0); } }
        public float YFloat { get { return BitConverter.ToSingle(BitConverter.GetBytes(YRaw), 0); } }

        public byte Attached; // equipment slots only

        public static Item Read(Reader r, int tailWords) { return Read(r, tailWords, tailWords == 4); }

        public static Item Read(Reader r, int tailWords, bool ascension)
        {
            Item it = new Item();
            it.Extra = new uint[tailWords];
            it.HasAscension = ascension;
            it.BaseName = r.Str();
            it.PrefixName = r.Str();
            it.SuffixName = r.Str();
            it.ModifierName = r.Str();
            it.TransmuteName = r.Str();
            it.Seed = r.U32();
            it.RelicName = r.Str();
            it.RelicBonus = r.Str();
            it.RelicSeed = r.U32();
            it.AugmentName = r.Str();
            it.Unknown = r.U32();
            it.AugmentSeed = r.U32();
            if (ascension) { it.AscensionName = r.Str(); it.AscensionSeed = r.U32(); }
            for (int i = 0; i < tailWords; i++) it.Extra[i] = r.U32();
            return it;
        }

        public void Write(Writer w)
        {
            w.Str(BaseName); w.Str(PrefixName); w.Str(SuffixName); w.Str(ModifierName); w.Str(TransmuteName);
            w.U32(Seed);
            w.Str(RelicName); w.Str(RelicBonus); w.U32(RelicSeed);
            w.Str(AugmentName); w.U32(Unknown); w.U32(AugmentSeed);
            if (HasAscension) { w.Str(AscensionName); w.U32(AscensionSeed); }
            for (int i = 0; i < Extra.Length; i++) w.U32(Extra[i]);
        }

        // A fresh item for a given save layout; the game fills in everything else
        // from the base record and the seed the first time it loads the character.
        // Four trailing words only ever occur together with the ascension field,
        // which is what lets the shorter call work out the shape on its own.
        public static Item Create(string baseName, int tailWords)
        { return Create(baseName, tailWords, tailWords == 4); }

        public static Item Create(string baseName, int tailWords, bool ascension)
        {
            Item it = new Item();
            it.Extra = new uint[tailWords];
            it.HasAscension = ascension;
            it.BaseName = baseName;
            it.StackCount = 1;
            return it;
        }
    }

    public class Sack
    {
        public byte TempBool;
        public List<Item> Items = new List<Item>();
    }

    public class StashTab
    {
        public int Width, Height;
        public List<Item> Items = new List<Item>();
        public uint[] Tail = new uint[0]; // trailing zero words, five in current saves
    }

    public class Skill
    {
        public string Name = "";
        public int Level;
        public byte Enabled;
        public int DevotionLevel, DevotionExperience, SubLevel;
        public byte Active, Transition;
        public byte Unknown;
        public string AutoCastSkill = "", AutoCastController = "";
    }

    // A skill granted by an item. Seen only on one of the sample characters, so
    // the short form below is kept as a fallback layout.
    public class ItemSkill
    {
        public string SkillName = "";           // records/skills/itemskills*/...
        public string AutoCastSkill = "";       // devotion skill bound to it
        public string AutoCastController = "";  // records/controllers/itemskills/...
        public uint Unknown;
        public string ItemSet = "";             // records/items/lootsets/...
    }

    public class Faction
    {
        public byte Modified, Unlocked;
        public float Value, PositiveBoost, NegativeBoost;
    }

    // A block kept verbatim. Reading it byte by byte gives a plaintext that is
    // NOT what the game wrote: a four-byte field is decrypted with one key word,
    // whereas four single-byte reads use four different key words. The bytes only
    // survive a round trip because writing repeats the same mistake with the same
    // key stream. Change the seed or anything before the block and the game reads
    // rubbish. So this is a last resort for block ids we do not know; every block
    // that appears in real saves is parsed field by field.
    public class OpaqueBlock
    {
        public uint Id;
        public byte[] Payload;
    }

    // ---------------------------------------------------- block 14: ui settings

    // One slot of the skill bar. Only skills and items carry data; the other
    // type codes (seen: -1, 2, 3, 5) are just the four-byte code.
    public class UiSlot
    {
        public const int TypeSkill = 0;
        public const int TypeItem = 4;

        public int Type;
        public string Skill = "";
        public byte IsItemSkill;
        public string Item = "";
        public int EquipLocation;
        public string BitmapUp = "", BitmapDown = "", Label = "";

        public void Read(Reader r)
        {
            Type = r.I32();
            if (Type == TypeSkill)
            {
                Skill = r.Str(); IsItemSkill = r.U8(); Item = r.Str(); EquipLocation = r.I32();
            }
            else if (Type == TypeItem)
            {
                Item = r.Str(); BitmapUp = r.Str(); BitmapDown = r.Str(); Label = r.WStr();
            }
        }

        public void Write(Writer w)
        {
            w.I32(Type);
            if (Type == TypeSkill)
            {
                w.Str(Skill); w.U8(IsItemSkill); w.Str(Item); w.I32(EquipLocation);
            }
            else if (Type == TypeItem)
            {
                w.Str(Item); w.Str(BitmapUp); w.Str(BitmapDown); w.WStr(Label);
            }
        }
    }

    public class UiSkillSet
    {
        public int Id;
        public List<UiSlot> Slots = new List<UiSlot>();
    }

    public class UiSettings
    {
        public uint Version;
        public byte EquipmentSelection;
        public int SkillWindowSelection;
        public byte SkillSettingValid;
        public string[] PrimarySkill = new string[5];
        public string[] SecondarySkill = new string[5];
        public byte[] SkillActive = new byte[5];
        public int NumSkillSet = 1, SlotsPerSet;
        public List<UiSkillSet> SkillSets = new List<UiSkillSet>();
        public float CameraDistance;

        // Older layouts have a single skill set with a fixed number of slots.
        static int DefaultSlots(uint version)
        {
            if (version >= 6) return 47;
            if (version >= 5) return 46;
            return 36;
        }

        public void Read(Reader r)
        {
            Version = r.U32();
            EquipmentSelection = r.U8();
            SkillWindowSelection = r.I32();
            SkillSettingValid = r.U8();
            for (int i = 0; i < 5; i++)
            {
                PrimarySkill[i] = r.Str();
                SecondarySkill[i] = r.Str();
                SkillActive[i] = r.U8();
            }
            if (Version >= 7) { NumSkillSet = r.I32(); SlotsPerSet = r.I32(); }
            else { NumSkillSet = 1; SlotsPerSet = DefaultSlots(Version); }
            if (NumSkillSet < 0 || NumSkillSet > 64 || SlotsPerSet < 0 || SlotsPerSet > 1024)
                throw new GdcException("block 14: " + NumSkillSet + " skill sets of " + SlotsPerSet + " slots");
            SkillSets.Clear();
            for (int s = 0; s < NumSkillSet; s++)
            {
                UiSkillSet set = new UiSkillSet();
                if (Version >= 7) set.Id = r.I32();
                for (int i = 0; i < SlotsPerSet; i++)
                {
                    UiSlot slot = new UiSlot();
                    slot.Read(r);
                    set.Slots.Add(slot);
                }
                SkillSets.Add(set);
            }
            CameraDistance = r.F32();
        }

        public void Write(Writer w)
        {
            w.U32(Version);
            w.U8(EquipmentSelection);
            w.I32(SkillWindowSelection);
            w.U8(SkillSettingValid);
            for (int i = 0; i < 5; i++)
            {
                w.Str(PrimarySkill[i]); w.Str(SecondarySkill[i]); w.U8(SkillActive[i]);
            }
            if (Version >= 7) { w.I32(SkillSets.Count); w.I32(SlotsPerSet); }
            for (int s = 0; s < SkillSets.Count; s++)
            {
                if (Version >= 7) w.I32(SkillSets[s].Id);
                for (int i = 0; i < SkillSets[s].Slots.Count; i++) SkillSets[s].Slots[i].Write(w);
            }
            w.F32(CameraDistance);
        }
    }

    // ------------------------------------------------------ block 16: statistics

    public class DifficultyStats
    {
        public string GreatestMonsterKilledName = "";
        public int GreatestMonsterKilledLevel, GreatestMonsterKilledLifeAndMana;
        public string LastMonsterHit = "", LastMonsterHitBy = "";
        public int NemesisKills;
    }

    public class SkillMapEntry
    {
        public string Skill = "";
        public int Active;
    }

    public class Stats
    {
        public uint Version;
        public int PlayTime, Deaths, Kills, ExperienceFromKills, HealthPotionsUsed, ManaPotionsUsed;
        public int MaxLevel, HitsReceived, HitsInflicted, CriticalHitsInflicted, CriticalHitsReceived;
        public float GreatestDamageInflicted;
        public DifficultyStats[] PerDifficulty = new DifficultyStats[3];
        public int ChampionKills;
        public float LastHit, LastHitBy, GreatestDamageReceived;
        public int HeroKills, ItemsCrafted, RelicsCrafted, TranscendentRelicsCrafted, MythicalRelicsCrafted;
        public int ShrinesRestored, OneShotChestsOpened, LoreNotesCollected;
        public int SurvivalGreatestWave, SurvivalGreatestScore, SurvivalDefensesBuilt, SurvivalPowerUpsActivated;
        public List<SkillMapEntry> SkillMap = new List<SkillMapEntry>();
        public int EndlessSouls, EndlessEssence;
        public byte DifficultySkip;
        public int Unknown1, Unknown2;
        public int AscendantBossMonstersKilled, HiddenChestsOpened;

        public Stats() { for (int i = 0; i < 3; i++) PerDifficulty[i] = new DifficultyStats(); }

        public void Read(Reader r)
        {
            Version = r.U32();
            PlayTime = r.I32(); Deaths = r.I32(); Kills = r.I32(); ExperienceFromKills = r.I32();
            HealthPotionsUsed = r.I32(); ManaPotionsUsed = r.I32(); MaxLevel = r.I32();
            HitsReceived = r.I32(); HitsInflicted = r.I32();
            CriticalHitsInflicted = r.I32(); CriticalHitsReceived = r.I32();
            GreatestDamageInflicted = r.F32();
            for (int i = 0; i < 3; i++)
            {
                DifficultyStats d = PerDifficulty[i];
                d.GreatestMonsterKilledName = r.Str();
                d.GreatestMonsterKilledLevel = r.I32();
                d.GreatestMonsterKilledLifeAndMana = r.I32();
                d.LastMonsterHit = r.Str();
                d.LastMonsterHitBy = r.Str();
            }
            ChampionKills = r.I32();
            LastHit = r.F32(); LastHitBy = r.F32(); GreatestDamageReceived = r.F32();
            HeroKills = r.I32(); ItemsCrafted = r.I32(); RelicsCrafted = r.I32();
            TranscendentRelicsCrafted = r.I32(); MythicalRelicsCrafted = r.I32();
            ShrinesRestored = r.I32(); OneShotChestsOpened = r.I32(); LoreNotesCollected = r.I32();
            for (int i = 0; i < 3; i++) PerDifficulty[i].NemesisKills = r.I32();
            if (Version >= 9)
            {
                SurvivalGreatestWave = r.I32(); SurvivalGreatestScore = r.I32();
                SurvivalDefensesBuilt = r.I32(); SurvivalPowerUpsActivated = r.I32();
            }
            if (Version >= 11)
            {
                SkillMap.Clear();
                int n = r.I32();
                if (n < 0 || n > 100000) throw new GdcException("block 16: skill map of " + n + " entries");
                for (int i = 0; i < n; i++)
                {
                    SkillMapEntry e = new SkillMapEntry();
                    e.Skill = r.Str(); e.Active = r.I32();
                    SkillMap.Add(e);
                }
                EndlessSouls = r.I32(); EndlessEssence = r.I32(); DifficultySkip = r.U8();
            }
            Unknown1 = r.I32(); Unknown2 = r.I32();
            if (Version >= 12) { AscendantBossMonstersKilled = r.I32(); HiddenChestsOpened = r.I32(); }
        }

        public void Write(Writer w)
        {
            w.U32(Version);
            w.I32(PlayTime); w.I32(Deaths); w.I32(Kills); w.I32(ExperienceFromKills);
            w.I32(HealthPotionsUsed); w.I32(ManaPotionsUsed); w.I32(MaxLevel);
            w.I32(HitsReceived); w.I32(HitsInflicted);
            w.I32(CriticalHitsInflicted); w.I32(CriticalHitsReceived);
            w.F32(GreatestDamageInflicted);
            for (int i = 0; i < 3; i++)
            {
                DifficultyStats d = PerDifficulty[i];
                w.Str(d.GreatestMonsterKilledName);
                w.I32(d.GreatestMonsterKilledLevel);
                w.I32(d.GreatestMonsterKilledLifeAndMana);
                w.Str(d.LastMonsterHit);
                w.Str(d.LastMonsterHitBy);
            }
            w.I32(ChampionKills);
            w.F32(LastHit); w.F32(LastHitBy); w.F32(GreatestDamageReceived);
            w.I32(HeroKills); w.I32(ItemsCrafted); w.I32(RelicsCrafted);
            w.I32(TranscendentRelicsCrafted); w.I32(MythicalRelicsCrafted);
            w.I32(ShrinesRestored); w.I32(OneShotChestsOpened); w.I32(LoreNotesCollected);
            for (int i = 0; i < 3; i++) w.I32(PerDifficulty[i].NemesisKills);
            if (Version >= 9)
            {
                w.I32(SurvivalGreatestWave); w.I32(SurvivalGreatestScore);
                w.I32(SurvivalDefensesBuilt); w.I32(SurvivalPowerUpsActivated);
            }
            if (Version >= 11)
            {
                w.I32(SkillMap.Count);
                for (int i = 0; i < SkillMap.Count; i++) { w.Str(SkillMap[i].Skill); w.I32(SkillMap[i].Active); }
                w.I32(EndlessSouls); w.I32(EndlessEssence); w.U8(DifficultySkip);
            }
            w.I32(Unknown1); w.I32(Unknown2);
            if (Version >= 12) { w.I32(AscendantBossMonstersKilled); w.I32(HiddenChestsOpened); }
        }
    }

    public class Character
    {
        public uint Seed;                    // cipher seed, preserved for byte-identical saves
        public uint Version;                 // file version (2)
        public string Name = "";
        public byte Sex;
        public string ClassTag = "";
        public int Level;
        public byte Hardcore;
        public byte Expansion;
        public uint DataVersion;             // 8 for current builds
        public byte[] Mystery = new byte[16];

        // block 1
        public uint InfoVersion;
        public byte InMainQuest, HasBeenInGame;
        public byte LastDifficulty, GreatestDifficultyCompleted;
        public int Money;
        public byte GreatestSurvivalDifficultyCompleted;
        public int Tributes;
        public byte CompassState, SkillWindowShowHelp, AlternateConfig, AlternateConfigEnabled;
        public string Texture = "";
        public byte[] LootFilters = new byte[0];

        // block 2
        public uint BioVersion;
        public int Experience, AttributePoints, SkillPoints, DevotionPoints, TotalDevotion;
        public float Physique, Cunning, Spirit, Health, Energy;

        // block 3
        public uint InventoryVersion;
        public byte InventoryHasData;
        public int FocusedSack, SelectedSack;
        public List<Sack> Sacks = new List<Sack>();
        public byte UseAlternate, Alternate1, Alternate2;
        public List<Item> Equipment = new List<Item>();   // 12 armour/jewellery slots
        public List<Item> Weapons1 = new List<Item>();    // main weapon set, 2 slots
        public List<Item> Weapons2 = new List<Item>();    // alternate weapon set, 2 slots

        // block 4
        public uint StashVersion;
        public List<StashTab> StashTabs = new List<StashTab>();

        // block 8
        public uint SkillsVersion;
        public List<Skill> Skills = new List<Skill>();
        public int MasteriesAllowed, SkillReclamationPointsUsed, DevotionReclamationPointsUsed;
        public List<ItemSkill> ItemSkills = new List<ItemSkill>();

        // block 10 / 12 — one string list per difficulty
        public uint TokensVersion, LoreVersion;
        public List<List<string>> Tokens = new List<List<string>>();
        public List<List<string>> LoreNotes = new List<List<string>>();

        // block 13
        public uint FactionsVersion;
        public int MyFaction;
        public List<Faction> Factions = new List<Faction>();

        // blocks 5, 6, 7, 17 — UID lists (respawn points, riftgates, map markers, shrines)
        public Dictionary<uint, List<byte[]>> UidLists = new Dictionary<uint, List<byte[]>>();
        public Dictionary<uint, uint> UidListVersions = new Dictionary<uint, uint>();
        // block 5 also records the currently active respawn point per difficulty
        public List<byte[]> CurrentRespawn = new List<byte[]>();

        // block 14
        public UiSettings Ui = new UiSettings();

        // block 15
        public uint TutorialsVersion;
        public List<int> TutorialPages = new List<int>();

        // block 16
        public Stats Stats = new Stats();

        // Any block id we do not know. Kept verbatim, which is only correct as
        // long as nothing before it moves and the seed stays the same — see
        // OpaqueBlock. None of the fifteen sample saves has one.
        public List<OpaqueBlock> Opaque = new List<OpaqueBlock>();

        // Set when a block had to fall back to the verbatim path, so callers can
        // refuse to write a file that would not survive a new key stream.
        public List<uint> UnparsedBlocks = new List<uint>();
        public bool SafeToRewrite { get { return UnparsedBlocks.Count == 0; } }

        // Order in which blocks appeared, so a save reproduces the original layout.
        public List<uint> BlockOrder = new List<uint>();

        // Layout details that differ between game versions. They are detected on
        // load by parsing a block each way and keeping whichever lands exactly on
        // the block terminator, then reused when writing the file back.
        public int ItemTailWords = 4;       // 4 with ascension, 6 before it, 2 in the oldest saves
        public bool ItemHasAscension = true;
        public int StashTabTailWords = 5;   // 5 in current saves, 0 in older ones
        public bool SkillsHaveExtraByte = true;
        public bool SkillsHaveTrailer = true;
        public bool ItemSkillLongForm = true;
        public bool RespawnHasCurrent = true;
        public Dictionary<uint, int> UidListCounts = new Dictionary<uint, int>();
        public Dictionary<uint, int> StringListCounts = new Dictionary<uint, int>();

        public class BlockInfo
        {
            public uint Id;
            public int Offset, PayloadLength, TotalLength;
            public string Name;
        }
        public List<BlockInfo> BlockMap = new List<BlockInfo>();

        public static string BlockName(uint id)
        {
            switch (id)
            {
                case 1: return "character info";
                case 2: return "bio / attributes";
                case 3: return "inventory";
                case 4: return "stash";
                case 5: return "respawn points";
                case 6: return "riftgates";
                case 7: return "map markers";
                case 8: return "skills";
                case 10: return "quest tokens";
                case 12: return "lore notes";
                case 13: return "factions";
                case 14: return "ui settings";
                case 15: return "tutorials";
                case 16: return "statistics";
                case 17: return "shrines";
                default: return "unknown";
            }
        }

        public const uint Magic = 0x58434447; // "GDCX"

        // ------------------------------------------------- layout detection

        delegate void Attempt(int[] variant);
        delegate void Cleanup();

        // Parses a block by trying each candidate layout in turn. A layout is
        // accepted only when the parse consumes the payload exactly; anything
        // else is rewound and the next candidate is tried.
        //
        // If no layout fits, report how many bytes each layout read against the block
        // length and what stopped it. This exposes missing or extra bytes and the
        // field where parsing failed. Added on August 31 after a Berserker save
        // failed to load at the skills block.
        static int[] Detect(Reader r, int payloadEnd, uint id, int[][] variants, Attempt parse, Cleanup reset)
        {
            Reader.State start = r.Mark();
            System.Text.StringBuilder why = new System.Text.StringBuilder();
            int best = -1, bestPos = -1;
            for (int i = 0; i < variants.Length; i++)
            {
                reset();
                bool ok = false;
                string note = null;
                try { parse(variants[i]); ok = r.Pos == payloadEnd; }
                catch (GdcException e) { note = e.Message; }
                catch (IndexOutOfRangeException) { note = "read past the end of the block"; }
                catch (ArgumentException e) { note = e.Message; }
                if (ok) return variants[i];
                if (r.Pos > bestPos) { bestPos = r.Pos; best = i; }
                why.Append("\n  layout {");
                for (int k = 0; k < variants[i].Length; k++) why.Append(k > 0 ? "," : "").Append(variants[i][k]);
                why.Append("}: read ").Append(r.Pos - start.Pos)
                   .Append(" of ").Append(payloadEnd - start.Pos).Append(" bytes");
                if (note != null) why.Append(" — ").Append(note);
                r.Restore(start);
            }
            reset();
            // Show the unread bytes after the layout that read furthest, in hex and
            // as text. Record names are stored as plain text and can reveal what
            // the game wrote in the remainder of the block.
            if (best >= 0)
            {
                reset();
                try { parse(variants[best]); } catch (Exception) { }
                int left = payloadEnd - r.Pos;
                if (left > 0 && left < 4096)
                {
                    byte[] tail = r.Bytes(left);
                    System.Text.StringBuilder hex = new System.Text.StringBuilder();
                    System.Text.StringBuilder txt = new System.Text.StringBuilder();
                    for (int i = 0; i < tail.Length; i++)
                    {
                        hex.Append(tail[i].ToString("X2")).Append(i % 16 == 15 ? "\n    " : " ");
                        txt.Append(tail[i] >= 32 && tail[i] < 127 ? (char)tail[i] : '.');
                    }
                    why.Append("\n  unread tail, ").Append(left).Append(" bytes:\n    ")
                       .Append(hex).Append("\n  as text:\n    ").Append(txt);
                }
                r.Restore(start);
                reset();
            }
            throw new GdcException("block " + id + ": none of the known layouts fits its " +
                                   (payloadEnd - start.Pos) + " byte payload" + why);
        }

        void LoadInventory(Reader r, int end)
        {
            Cleanup reset = delegate
            {
                Sacks.Clear(); Equipment.Clear(); Weapons1.Clear(); Weapons2.Clear();
            };
            // {tail words, ascension field present}. The ascended shape is tried
            // first: a save with nothing ascended fits it and the six-word shape
            // alike — the empty string occupies the same four zero bytes — so
            // either reading rebuilds the file byte for byte, and this one also
            // copes the moment something does get ascended.
            int[][] variants = new int[][] {
                new int[] { 4, 1 }, new int[] { 6, 0 }, new int[] { 2, 0 },
                new int[] { 4, 0 }, new int[] { 6, 1 }, new int[] { 2, 1 } };
            int[] v = Detect(r, end, 3, variants,
                delegate (int[] p) { ItemTailWords = p[0]; ItemHasAscension = p[1] != 0; ReadInventory(r); }, reset);
            ItemTailWords = v[0]; ItemHasAscension = v[1] != 0;
        }

        void LoadStash(Reader r, int end)
        {
            Cleanup reset = delegate { StashTabs.Clear(); };
            // The item layout is already known from the inventory, so try that
            // first and only fall back to the full grid if it does not fit.
            int asc = ItemHasAscension ? 1 : 0;
            int[][] variants = new int[][] {
                new int[] { ItemTailWords, 5, asc }, new int[] { ItemTailWords, 0, asc },
                new int[] { 4, 5, 1 }, new int[] { 6, 5, 0 }, new int[] { 2, 0, 0 },
                new int[] { 6, 0, 0 }, new int[] { 2, 5, 0 }, new int[] { 4, 0, 1 } };
            int[] v = Detect(r, end, 4, variants, delegate (int[] p)
            {
                ItemTailWords = p[0]; StashTabTailWords = p[1]; ItemHasAscension = p[2] != 0;
                ReadStash(r);
            }, reset);
            ItemTailWords = v[0]; StashTabTailWords = v[1]; ItemHasAscension = v[2] != 0;
        }

        void LoadSkills(Reader r, int end)
        {
            Cleanup reset = delegate { Skills.Clear(); ItemSkills.Clear(); };
            // Three independent yes/no differences between game versions, tried
            // with the two known-good combinations first.
            int[][] variants = new int[][] {
                new int[] { 1, 1, 1 }, new int[] { 0, 0, 1 },
                new int[] { 1, 1, 0 }, new int[] { 0, 0, 0 },
                new int[] { 1, 0, 1 }, new int[] { 0, 1, 1 },
                new int[] { 1, 0, 0 }, new int[] { 0, 1, 0 } };
            int[] v = Detect(r, end, 8, variants, delegate (int[] p)
            {
                SkillsHaveExtraByte = p[0] != 0;
                SkillsHaveTrailer = p[1] != 0;
                ItemSkillLongForm = p[2] != 0;
                ReadSkills(r);
            }, reset);
            SkillsHaveExtraByte = v[0] != 0;
            SkillsHaveTrailer = v[1] != 0;
            ItemSkillLongForm = v[2] != 0;
        }

        void LoadStringLists(Reader r, int end, uint id)
        {
            List<List<string>> into = id == 10 ? Tokens : LoreNotes;
            Cleanup reset = delegate { into.Clear(); };
            int[][] variants = id == 10
                ? new int[][] { new int[] { 3 }, new int[] { 1 } }
                : new int[][] { new int[] { 1 }, new int[] { 3 } };
            int[] v = Detect(r, end, id, variants, delegate (int[] p)
            {
                uint ver = ReadStringLists(r, into, p[0]);
                if (id == 10) TokensVersion = ver; else LoreVersion = ver;
            }, reset);
            StringListCounts[id] = v[0];
        }

        void LoadUidLists(Reader r, int end, uint id)
        {
            Cleanup reset = delegate { UidLists.Remove(id); if (id == 5) CurrentRespawn.Clear(); };
            int[][] variants = id == 17
                ? new int[][] { new int[] { 6, 0 }, new int[] { 3, 0 } }
                : new int[][] { new int[] { 3, id == 5 ? 1 : 0 }, new int[] { 3, id == 5 ? 0 : 1 },
                                new int[] { 6, 0 } };
            int[] v = Detect(r, end, id, variants,
                delegate (int[] p) { ReadUidLists(r, id, p[0], p[1] != 0); }, reset);
            UidListCounts[id] = v[0];
            if (id == 5) RespawnHasCurrent = v[1] != 0;
        }

        delegate void Parse();

        // Blocks with a single known layout. If the parse does not land exactly on
        // the block terminator the save is from a build we do not know, so the
        // block is kept verbatim and the character is marked unsafe to rewrite.
        void LoadStructured(Reader r, int end, uint id, Parse parse)
        {
            Reader.State start = r.Mark();
            bool ok = false;
            try { parse(); ok = r.Pos == end; }
            catch (GdcException) { }
            catch (IndexOutOfRangeException) { }
            catch (ArgumentException) { }
            if (ok) return;
            r.Restore(start);
            ReadOpaque(r, end, id);
        }

        void ReadOpaque(Reader r, int end, uint id)
        {
            OpaqueBlock ob = new OpaqueBlock();
            ob.Id = id;
            ob.Payload = r.Bytes(end - r.Pos);
            Opaque.Add(ob);
            UnparsedBlocks.Add(id);
        }

        // ------------------------------------------------------------- load

        public static Character Load(string path)
        {
            Reader r = new Reader(File.ReadAllBytes(path));
            Character ch = new Character();
            ch.Seed = r.Seed;

            uint magic = r.U32();
            if (magic != Magic)
                throw new GdcException("not a player.gdc: magic is 0x" + magic.ToString("X8") + ", expected 0x58434447");
            ch.Version = r.U32();
            if (ch.Version != 1 && ch.Version != 2)
                throw new GdcException("unsupported file version " + ch.Version);

            ch.Name = r.WStr();
            ch.Sex = r.U8();
            ch.ClassTag = r.Str();
            ch.Level = r.I32();
            ch.Hardcore = r.U8();
            ch.Expansion = r.U8();
            uint term = r.U32(false);
            if (term != 0) throw new GdcException("header terminator is 0x" + term.ToString("X8") + ", expected 0");
            ch.DataVersion = r.U32();
            if (ch.DataVersion < 6 || ch.DataVersion > 12)
                throw new GdcException("unexpected data version " + ch.DataVersion);
            ch.Mystery = r.Bytes(16);

            while (!r.AtEnd)
            {
                int blockStart = r.Pos;
                int end;
                uint id = r.BlockStart(out end);
                ch.BlockOrder.Add(id);
                BlockInfo bi = new BlockInfo();
                bi.Id = id; bi.Offset = blockStart; bi.PayloadLength = end - r.Pos;
                bi.TotalLength = bi.PayloadLength + 12; bi.Name = BlockName(id);
                ch.BlockMap.Add(bi);
                switch (id)
                {
                    case 1: ch.ReadInfo(r); break;
                    case 2: ch.ReadBio(r); break;
                    case 3: ch.LoadInventory(r, end); break;
                    case 4: ch.LoadStash(r, end); break;
                    case 8: ch.LoadSkills(r, end); break;
                    case 10: ch.LoadStringLists(r, end, 10); break;
                    case 12: ch.LoadStringLists(r, end, 12); break;
                    case 13: ch.ReadFactions(r); break;
                    case 5: case 6: case 7: case 17: ch.LoadUidLists(r, end, id); break;
                    case 14: ch.LoadStructured(r, end, id, delegate { ch.Ui.Read(r); }); break;
                    case 15: ch.LoadStructured(r, end, id, delegate { ch.ReadTutorials(r); }); break;
                    case 16: ch.LoadStructured(r, end, id, delegate { ch.Stats.Read(r); }); break;
                    default:
                        ch.ReadOpaque(r, end, id);
                        break;
                }
                r.BlockEnd(end, id);
            }
            return ch;
        }

        void ReadInfo(Reader r)
        {
            InfoVersion = r.U32();
            InMainQuest = r.U8();
            HasBeenInGame = r.U8();
            LastDifficulty = r.U8();
            GreatestDifficultyCompleted = r.U8();
            Money = r.I32();
            GreatestSurvivalDifficultyCompleted = r.U8();
            Tributes = r.I32();
            CompassState = r.U8();
            SkillWindowShowHelp = r.U8();
            AlternateConfig = r.U8();
            AlternateConfigEnabled = r.U8();
            Texture = r.Str();
            LootFilters = r.Bytes(r.I32());
        }

        void WriteInfo(Writer w)
        {
            w.U32(InfoVersion);
            w.U8(InMainQuest); w.U8(HasBeenInGame);
            w.U8(LastDifficulty); w.U8(GreatestDifficultyCompleted);
            w.I32(Money);
            w.U8(GreatestSurvivalDifficultyCompleted);
            w.I32(Tributes);
            w.U8(CompassState); w.U8(SkillWindowShowHelp); w.U8(AlternateConfig); w.U8(AlternateConfigEnabled);
            w.Str(Texture);
            w.I32(LootFilters.Length); w.Bytes(LootFilters);
        }

        void ReadBio(Reader r)
        {
            BioVersion = r.U32();
            Level = r.I32();
            Experience = r.I32();
            AttributePoints = r.I32();
            SkillPoints = r.I32();
            DevotionPoints = r.I32();
            TotalDevotion = r.I32();
            Physique = r.F32(); Cunning = r.F32(); Spirit = r.F32();
            Health = r.F32(); Energy = r.F32();
        }

        void WriteBio(Writer w)
        {
            w.U32(BioVersion);
            w.I32(Level); w.I32(Experience); w.I32(AttributePoints); w.I32(SkillPoints);
            w.I32(DevotionPoints); w.I32(TotalDevotion);
            w.F32(Physique); w.F32(Cunning); w.F32(Spirit); w.F32(Health); w.F32(Energy);
        }

        void ReadInventory(Reader r)
        {
            InventoryVersion = r.U32();
            InventoryHasData = r.U8();
            // For a character who has never entered the world, the inventory block
            // ends after the version and this zero flag. Its five-byte payload omits
            // the bag count, all twelve equipment slots, and both weapon sets.
            // The six layouts all failed to match it before this case was handled.
            //
            // Verified by a byte-for-byte round trip on _444, a Berserker created
            // in the menu. The inventory first appears when the character enters
            // the world and the intro plays.
            if (InventoryHasData == 0) return;
            int sackCount = r.I32();
            FocusedSack = r.I32();
            SelectedSack = r.I32();
            for (int s = 0; s < sackCount; s++)
            {
                int end;
                uint id = r.BlockStart(out end);
                Sack sack = new Sack();
                sack.TempBool = r.U8();
                int n = r.I32();
                for (int i = 0; i < n; i++)
                {
                    Item it = Item.Read(r, ItemTailWords, ItemHasAscension);
                    it.XRaw = r.U32(); it.YRaw = r.U32();
                    sack.Items.Add(it);
                }
                r.BlockEnd(end, id);
                Sacks.Add(sack);
            }
            UseAlternate = r.U8();
            for (int i = 0; i < 12; i++) Equipment.Add(ReadEquipped(r));
            Alternate1 = r.U8();
            for (int i = 0; i < 2; i++) Weapons1.Add(ReadEquipped(r));
            Alternate2 = r.U8();
            for (int i = 0; i < 2; i++) Weapons2.Add(ReadEquipped(r));
        }

        Item ReadEquipped(Reader r)
        {
            Item it = Item.Read(r, ItemTailWords, ItemHasAscension);
            it.Attached = r.U8();
            return it;
        }

        void WriteInventory(Writer w)
        {
            w.U32(InventoryVersion);
            w.U8(InventoryHasData);
            // Write the same short inventory block that ReadInventory reads to
            // preserve the byte-for-byte round trip.
            if (InventoryHasData == 0) return;
            w.I32(Sacks.Count); w.I32(FocusedSack); w.I32(SelectedSack);
            for (int s = 0; s < Sacks.Count; s++)
            {
                Writer.Block b = w.BlockStart(0);
                w.U8(Sacks[s].TempBool);
                w.I32(Sacks[s].Items.Count);
                for (int i = 0; i < Sacks[s].Items.Count; i++)
                {
                    Item it = Sacks[s].Items[i];
                    it.Write(w); w.U32(it.XRaw); w.U32(it.YRaw);
                }
                w.BlockEnd(b);
            }
            w.U8(UseAlternate);
            for (int i = 0; i < Equipment.Count; i++) { Equipment[i].Write(w); w.U8(Equipment[i].Attached); }
            w.U8(Alternate1);
            for (int i = 0; i < Weapons1.Count; i++) { Weapons1[i].Write(w); w.U8(Weapons1[i].Attached); }
            w.U8(Alternate2);
            for (int i = 0; i < Weapons2.Count; i++) { Weapons2[i].Write(w); w.U8(Weapons2[i].Attached); }
        }

        void ReadStash(Reader r)
        {
            StashVersion = r.U32();
            int tabs = r.I32();
            for (int t = 0; t < tabs; t++)
            {
                int end;
                uint id = r.BlockStart(out end);
                StashTab tab = new StashTab();
                tab.Width = r.I32(); tab.Height = r.I32();
                int n = r.I32();
                for (int i = 0; i < n; i++)
                {
                    Item it = Item.Read(r, ItemTailWords, ItemHasAscension);
                    it.XRaw = r.U32(); it.YRaw = r.U32();
                    tab.Items.Add(it);
                }
                tab.Tail = new uint[StashTabTailWords];
                for (int i = 0; i < tab.Tail.Length; i++) tab.Tail[i] = r.U32();
                r.BlockEnd(end, id);
                StashTabs.Add(tab);
            }
        }

        void WriteStash(Writer w)
        {
            w.U32(StashVersion);
            w.I32(StashTabs.Count);
            for (int t = 0; t < StashTabs.Count; t++)
            {
                StashTab tab = StashTabs[t];
                Writer.Block b = w.BlockStart(0);
                w.I32(tab.Width); w.I32(tab.Height); w.I32(tab.Items.Count);
                for (int i = 0; i < tab.Items.Count; i++)
                {
                    Item it = tab.Items[i];
                    it.Write(w); w.U32(it.XRaw); w.U32(it.YRaw);
                }
                for (int i = 0; i < tab.Tail.Length; i++) w.U32(tab.Tail[i]);
                w.BlockEnd(b);
            }
        }

        void ReadSkills(Reader r)
        {
            SkillsVersion = r.U32();
            int n = r.I32();
            for (int i = 0; i < n; i++)
            {
                Skill s = new Skill();
                s.Name = r.Str();
                s.Level = r.I32();
                s.Enabled = r.U8();
                if (SkillsHaveExtraByte) s.Unknown = r.U8();
                s.DevotionLevel = r.I32();
                s.DevotionExperience = r.I32();
                s.SubLevel = r.I32();
                s.Active = r.U8();
                s.Transition = r.U8();
                s.AutoCastSkill = r.Str();
                s.AutoCastController = r.Str();
                Skills.Add(s);
            }
            MasteriesAllowed = r.I32();
            SkillReclamationPointsUsed = r.I32();
            DevotionReclamationPointsUsed = r.I32();
            int m = r.I32();
            for (int i = 0; i < m; i++)
            {
                ItemSkill s = new ItemSkill();
                s.SkillName = r.Str();
                s.AutoCastSkill = r.Str();
                s.AutoCastController = r.Str();
                if (ItemSkillLongForm) { s.Unknown = r.U32(); s.ItemSet = r.Str(); }
                ItemSkills.Add(s);
            }
            // The skills block ends with a count of devotion bindings for form skills.
            // For a month and a half the old parser treated the count as a single trailer word
            // because it was zero in all tested saves. Berserker save _555 stored 3,
            // followed by three records of four strings each:
            //
            //     form skill                    devotion star       controller                      form
            //     werewolf1_skill01_claws       tier1_08e_skill      cast_@enemyonattackcrit_100%    werewolf1
            //     werewolf1_skill02_charge      tier3_13g_skill      cast_@selfonattackcrit_100%     werewolf1
            //     wereraven1_skill01_icicles    tier2_06g_skill      cast_@selfonattackcrit_100%     wereraven1
            //
            // Regular skills store bindings in AutoCastSkill and AutoCastController.
            // Form-granted skills have no entry in that list, so the fourth string
            // identifies the form. A zero count still occupies the same four bytes
            // and preserves the byte-for-byte round trip for older saves.
            if (SkillsHaveTrailer)
            {
                int t = r.I32();
                if (t < 0 || t > 100000) throw new GdcException("block 8: " + t + " form skill bindings");
                for (int i = 0; i < t; i++)
                {
                    FormSkill f = new FormSkill();
                    f.SkillName = r.Str();
                    f.AutoCastSkill = r.Str();
                    f.AutoCastController = r.Str();
                    f.Form = r.Str();
                    FormSkills.Add(f);
                }
            }
        }

        // A skill granted by a form such as Werewolf or Wereraven and the devotion
        // star bound to it. See ReadSkills.
        public class FormSkill
        {
            public string SkillName = "", AutoCastSkill = "", AutoCastController = "", Form = "";
        }
        public readonly List<FormSkill> FormSkills = new List<FormSkill>();


        void WriteSkills(Writer w)
        {
            w.U32(SkillsVersion);
            w.I32(Skills.Count);
            for (int i = 0; i < Skills.Count; i++)
            {
                Skill s = Skills[i];
                w.Str(s.Name); w.I32(s.Level); w.U8(s.Enabled);
                if (SkillsHaveExtraByte) w.U8(s.Unknown);
                w.I32(s.DevotionLevel); w.I32(s.DevotionExperience); w.I32(s.SubLevel);
                w.U8(s.Active); w.U8(s.Transition);
                w.Str(s.AutoCastSkill); w.Str(s.AutoCastController);
            }
            w.I32(MasteriesAllowed);
            w.I32(SkillReclamationPointsUsed);
            w.I32(DevotionReclamationPointsUsed);
            w.I32(ItemSkills.Count);
            for (int i = 0; i < ItemSkills.Count; i++)
            {
                w.Str(ItemSkills[i].SkillName);
                w.Str(ItemSkills[i].AutoCastSkill);
                w.Str(ItemSkills[i].AutoCastController);
                if (ItemSkillLongForm) { w.U32(ItemSkills[i].Unknown); w.Str(ItemSkills[i].ItemSet); }
            }
            if (SkillsHaveTrailer)
            {
                w.I32(FormSkills.Count);
                for (int i = 0; i < FormSkills.Count; i++)
                {
                    w.Str(FormSkills[i].SkillName);
                    w.Str(FormSkills[i].AutoCastSkill);
                    w.Str(FormSkills[i].AutoCastController);
                    w.Str(FormSkills[i].Form);
                }
            }
        }

        uint ReadStringLists(Reader r, List<List<string>> into, int lists)
        {
            uint ver = r.U32();
            for (int l = 0; l < lists; l++)
            {
                int n = r.I32();
                List<string> list = new List<string>();
                for (int i = 0; i < n; i++) list.Add(r.Str());
                into.Add(list);
            }
            return ver;
        }

        static void WriteStringLists(Writer w, uint ver, List<List<string>> lists)
        {
            w.U32(ver);
            for (int l = 0; l < lists.Count; l++)
            {
                w.I32(lists[l].Count);
                for (int i = 0; i < lists[l].Count; i++) w.Str(lists[l][i]);
            }
        }

        void ReadTutorials(Reader r)
        {
            TutorialsVersion = r.U32();
            TutorialPages.Clear();
            int n = r.I32();
            if (n < 0 || n > 100000) throw new GdcException("block 15: " + n + " tutorial pages");
            for (int i = 0; i < n; i++) TutorialPages.Add(r.I32());
        }

        void WriteTutorials(Writer w)
        {
            w.U32(TutorialsVersion);
            w.I32(TutorialPages.Count);
            for (int i = 0; i < TutorialPages.Count; i++) w.I32(TutorialPages[i]);
        }

        void ReadFactions(Reader r)
        {
            FactionsVersion = r.U32();
            MyFaction = r.I32();
            int n = r.I32();
            for (int i = 0; i < n; i++)
            {
                Faction f = new Faction();
                f.Modified = r.U8();
                f.Unlocked = r.U8();
                f.Value = r.F32();
                f.PositiveBoost = r.F32();
                f.NegativeBoost = r.F32();
                Factions.Add(f);
            }
        }

        void WriteFactions(Writer w)
        {
            w.U32(FactionsVersion);
            w.I32(MyFaction);
            w.I32(Factions.Count);
            for (int i = 0; i < Factions.Count; i++)
            {
                Faction f = Factions[i];
                w.U8(f.Modified); w.U8(f.Unlocked);
                w.F32(f.Value); w.F32(f.PositiveBoost); w.F32(f.NegativeBoost);
            }
        }

        void ReadUidLists(Reader r, uint id, int lists, bool hasCurrent)
        {
            UidListVersions[id] = r.U32();
            List<byte[]> all = new List<byte[]>();
            for (int l = 0; l < lists; l++)
            {
                int n = r.I32();
                for (int i = 0; i < n; i++) all.Add(r.Bytes(16));
                all.Add(null); // list separator, so the split survives a round trip
            }
            UidLists[id] = all;
            // The respawn block ends with the active respawn point of each difficulty.
            if (hasCurrent) for (int l = 0; l < lists; l++) CurrentRespawn.Add(r.Bytes(16));
        }

        void WriteUidLists(Writer w, uint id)
        {
            w.U32(UidListVersions[id]);
            List<byte[]> all = UidLists[id];
            int i = 0;
            while (i < all.Count)
            {
                int n = 0;
                while (i + n < all.Count && all[i + n] != null) n++;
                w.I32(n);
                for (int k = 0; k < n; k++) w.Bytes(all[i + k]);
                i += n + 1;
            }
            if (id == 5 && RespawnHasCurrent)
                for (int l = 0; l < CurrentRespawn.Count; l++) w.Bytes(CurrentRespawn[l]);
        }

        // ------------------------------------------------------------- save

        public byte[] ToBytes() { return ToBytes(Seed); }

        public byte[] ToBytes(uint seed)
        {
            Writer w = new Writer(seed);
            w.U32(Magic);
            w.U32(Version);
            w.WStr(Name);
            w.U8(Sex);
            w.Str(ClassTag);
            w.I32(Level);
            w.U8(Hardcore);
            w.U8(Expansion);
            w.U32(0, false);
            w.U32(DataVersion);
            w.Bytes(Mystery);

            for (int i = 0; i < BlockOrder.Count; i++)
            {
                uint id = BlockOrder[i];
                Writer.Block b = w.BlockStart(id);
                switch (id)
                {
                    case 1: WriteInfo(w); break;
                    case 2: WriteBio(w); break;
                    case 3: WriteInventory(w); break;
                    case 4: WriteStash(w); break;
                    case 8: WriteSkills(w); break;
                    case 10: WriteStringLists(w, TokensVersion, Tokens); break;
                    case 12: WriteStringLists(w, LoreVersion, LoreNotes); break;
                    case 13: WriteFactions(w); break;
                    case 5: case 6: case 7: case 17: WriteUidLists(w, id); break;
                    case 14: if (UnparsedBlocks.Contains(14)) goto default; Ui.Write(w); break;
                    case 15: if (UnparsedBlocks.Contains(15)) goto default; WriteTutorials(w); break;
                    case 16: if (UnparsedBlocks.Contains(16)) goto default; Stats.Write(w); break;
                    default:
                        {
                            OpaqueBlock ob = null;
                            for (int k = 0; k < Opaque.Count; k++) if (Opaque[k].Id == id) { ob = Opaque[k]; break; }
                            if (ob == null) throw new GdcException("no data for block " + id);
                            w.Bytes(ob.Payload);
                            break;
                        }
                }
                w.BlockEnd(b);
            }
            return w.ToArray();
        }

        public void Save(string path) { File.WriteAllBytes(path, ToBytes()); }

        // ------------------------------------------------------- convenience

        public int TotalInventoryItems
        {
            get { int n = 0; for (int i = 0; i < Sacks.Count; i++) n += Sacks[i].Items.Count; return n; }
        }

        public int TotalStashItems
        {
            get { int n = 0; for (int i = 0; i < StashTabs.Count; i++) n += StashTabs[i].Items.Count; return n; }
        }

        public List<Skill> LearnedSkills
        {
            get
            {
                List<Skill> r = new List<Skill>();
                for (int i = 0; i < Skills.Count; i++) if (Skills[i].Level > 0) r.Add(Skills[i]);
                return r;
            }
        }
    }
}
