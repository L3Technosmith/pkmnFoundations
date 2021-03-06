﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.Serialization;
using System.IO;
using PkmnFoundations.Support;

namespace PkmnFoundations.Structures
{
    /// <summary>
    /// Structure used to represent Pokémon on the GTS in Generation V.
    /// Includes a Pokémon box structure and metadata related to the trainer
    /// and request.
    /// </summary>
    public class GtsRecord5 : IEquatable<GtsRecord5>
    {
        // todo: We should have a base class for Gen4/5 GTS records.

        public GtsRecord5()
        {
        }

        public GtsRecord5(byte[] data)
        {
            Load(data);
        }

        // xxx: Data and Unknown0 should be one field.

        /// <summary>
        /// Obfuscated Pokémon (pkm) data. 220 bytes
        /// </summary>
        public byte[] Data;

        /// <summary>
        /// Unknown padding between pkm and rest of data. 16 bytes.
        /// </summary>
        public byte[] Unknown0;

        /// <summary>
        /// National Dex species number
        /// </summary>
        public ushort Species;

        /// <summary>
        /// Pokémon gender
        /// </summary>
        public Genders Gender;

        /// <summary>
        /// Pokémon level
        /// </summary>
        public byte Level;

        /// <summary>
        /// Requested National Dex species number
        /// </summary>
        public ushort RequestedSpecies;

        public Genders RequestedGender;

        public byte RequestedMinLevel;
        public byte RequestedMaxLevel;
        public byte Unknown1;
        public TrainerGenders TrainerGender;
        public byte Unknown2;

        public DateTime ? TimeDeposited;
        public DateTime ? TimeExchanged;

        /// <summary>
        /// User ID of the player (not Personality Value)
        /// </summary>
        public int PID;

        public uint TrainerOT;

        /// <summary>
        /// 16 bytes
        /// </summary>
        public EncodedString5 TrainerName;

        public byte TrainerCountry;
        public byte TrainerRegion;
        public byte TrainerClass;

        public byte IsExchanged;

        public byte TrainerVersion;
        public byte TrainerLanguage;

        public byte TrainerBadges; // speculative. Usually 8.
        public byte TrainerUnityTower;

        public byte[] Save()
        {
            // todo: enclose in properties and validate these when assigning.
            if (Data.Length != 0xDC) throw new FormatException("PKM length is incorrect");
            if (TrainerName.RawData.Length != 0x10) throw new FormatException("Trainer name length is incorrect");
            byte[] data = new byte[296];
            MemoryStream s = new MemoryStream(data);
            s.Write(Data, 0, 0xDC);
            s.Write(Unknown0, 0, 0x10);
            s.Write(BitConverter.GetBytes(Species), 0, 2);
            s.WriteByte((byte)Gender);
            s.WriteByte(Level);
            s.Write(BitConverter.GetBytes(RequestedSpecies), 0, 2);
            s.WriteByte((byte)RequestedGender);
            s.WriteByte(RequestedMinLevel);
            s.WriteByte(RequestedMaxLevel);
            s.WriteByte(Unknown1);
            s.WriteByte((byte)TrainerGender);
            s.WriteByte(Unknown2);
            s.Write(BitConverter.GetBytes(GtsRecord4.DateToTimestamp(TimeDeposited)), 0, 8);
            s.Write(BitConverter.GetBytes(GtsRecord4.DateToTimestamp(TimeExchanged)), 0, 8);
            s.Write(BitConverter.GetBytes(PID), 0, 4);
            s.Write(BitConverter.GetBytes(TrainerOT), 0, 4);
            s.Write(TrainerName.RawData, 0, 0x10);
            s.WriteByte(TrainerCountry);
            s.WriteByte(TrainerRegion);
            s.WriteByte(TrainerClass);
            s.WriteByte(IsExchanged);
            s.WriteByte(TrainerVersion);
            s.WriteByte(TrainerLanguage);
            s.WriteByte(TrainerBadges);
            s.WriteByte(TrainerUnityTower);
            s.Close();
            return data;
        }

        public void Load(byte[] data)
        {
            if (data.Length != 296) throw new FormatException("GTS record length is incorrect.");

            Data = new byte[0xDC];
            Array.Copy(data, 0, Data, 0, 0xDC);
            Unknown0 = new byte[0x10];
            Array.Copy(data, 0xDC, Unknown0, 0, 0x10);
            Species = BitConverter.ToUInt16(data, 0xEC);
            Gender = (Genders)data[0xEE];
            Level = data[0xEF];
            RequestedSpecies = BitConverter.ToUInt16(data, 0xF0);
            RequestedGender = (Genders)data[0xF2];
            RequestedMinLevel = data[0xF3];
            RequestedMaxLevel = data[0xF4];
            Unknown1 = data[0xF5];
            TrainerGender = (TrainerGenders)data[0xF6];
            Unknown2 = data[0xF7];
            TimeDeposited = GtsRecord4.TimestampToDate(BitConverter.ToUInt64(data, 0xF8));
            TimeExchanged = GtsRecord4.TimestampToDate(BitConverter.ToUInt64(data, 0x100));
            PID = BitConverter.ToInt32(data, 0x108);
            TrainerOT = BitConverter.ToUInt32(data, 0x10C);
            TrainerName = new EncodedString5(data, 0x110, 0x10);
            TrainerCountry = data[0x120];
            TrainerRegion = data[0x121];
            TrainerClass = data[0x122];
            IsExchanged = data[0x123];
            TrainerVersion = data[0x124];
            TrainerLanguage = data[0x125];
            TrainerBadges = data[0x126];
            TrainerUnityTower = data[0x127];
        }

        public GtsRecord5 Clone()
        {
            // todo: I am not very efficient
            return new GtsRecord5(Save());
        }

        public bool Validate()
        {
            // todo: a. legitimacy check, and b. check that pkm data matches metadata
            return true;
        }

        public bool CanTrade(GtsRecord5 other)
        {
            if (IsExchanged != 0 || other.IsExchanged != 0) return false;

            if (Species != other.RequestedSpecies) return false;
            if (other.RequestedGender != Genders.Either && Gender != other.RequestedGender) return false;
            if (!CheckLevels(other.RequestedMinLevel, other.RequestedMaxLevel, Level)) return false;
            
            if (RequestedSpecies != other.Species) return false;
            if (RequestedGender != Genders.Either && other.Gender != RequestedGender) return false;
            if (!CheckLevels(RequestedMinLevel, RequestedMaxLevel, other.Level)) return false;

            return true;
        }

        public void FlagTraded(GtsRecord5 other)
        {
            Species = other.Species;
            Gender = other.Gender;
            Level = other.Level;
            RequestedSpecies = other.RequestedSpecies;
            RequestedGender = other.RequestedGender;
            RequestedMinLevel = other.RequestedMinLevel;
            RequestedMaxLevel = other.RequestedMaxLevel;
            TimeDeposited = other.TimeDeposited;
            TimeExchanged = DateTime.UtcNow;
            PID = other.PID;
            IsExchanged = 0x01;
        }

        public static bool CheckLevels(byte min, byte max, byte other)
        {
            if (max == 0) max = 255;
            return other >= min && other <= max;
        }

        public static bool operator ==(GtsRecord5 a, GtsRecord5 b)
        {
            if ((object)a == null && (object)b == null) return true;
            if ((object)a == null || (object)b == null) return false;
            // todo: optimize me
            return a.Save().SequenceEqual(b.Save());
        }

        public static bool operator !=(GtsRecord5 a, GtsRecord5 b)
        {
            return !(a == b);
        }

        public override bool Equals(object obj)
        {
            return this.Equals(obj as GtsRecord5);
        }

        public bool Equals(GtsRecord5 other)
        {
            return this == other;
        }

        public override int GetHashCode()
        {
            return ((int)GtsRecord4.DateToBinary(TimeDeposited) + (int)GtsRecord4.DateToBinary(TimeExchanged)) ^ PID;
        }
    }
}
