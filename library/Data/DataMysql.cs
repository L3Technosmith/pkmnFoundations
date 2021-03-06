﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using MySql.Data.MySqlClient;
using PkmnFoundations.Structures;
using PkmnFoundations.Support;
using System.Security.Cryptography;
using PkmnFoundations.Pokedex;

namespace PkmnFoundations.Data
{
    // todo: This class is getting quite large. We should move some things with
    // limited usefulness (eg. database creation) into separate classes.
    public class DataMysql : Database
    {
        #region Initialization
        public DataMysql(String connString)
        {
            ConnectionString = connString;
        }

        public String ConnectionString { get; set; }

        private MySqlConnection CreateConnection()
        {
            return new MySqlConnection(ConnectionString);
        }
        #endregion

        #region Utility
        public static String SqlSanitize(String s)
        {
            return SqlSanitize(s, "");
        }

        public static String SqlSanitize(String s, String newChar)
        {
            String result = s.Replace("\'", newChar).Replace("[", newChar).Replace("]", newChar).Replace("`", newChar);
            int x = result.IndexOf("--");
            if (x != -1) result = result.Substring(0, x);
            return result;
        }
        #endregion

        #region GTS 4
        public GtsRecord4 GtsDataForUser4(MySqlTransaction tran, int pid)
        {
            MySqlDataReader reader = (MySqlDataReader)tran.ExecuteReader("SELECT Data, Species, Gender, Level, " +
                "RequestedSpecies, RequestedGender, RequestedMinLevel, RequestedMaxLevel, " +
                "Unknown1, TrainerGender, Unknown2, TimeDeposited, TimeExchanged, pid, " +
                "TrainerName, TrainerOT, TrainerCountry, TrainerRegion, TrainerClass, " +
                "IsExchanged, TrainerVersion, TrainerLanguage FROM GtsPokemon4 WHERE pid = @pid",
                new MySqlParameter("@pid", pid));

            if (!reader.Read())
            {
                reader.Close();
                return null;
            }
            GtsRecord4 result = Record4FromReader(reader);
#if DEBUG
            AssertHelper.Equals(result.PID, pid);
#endif
            reader.Close();
            return result;
        }

        public override GtsRecord4 GtsDataForUser4(int pid)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    GtsRecord4 result = GtsDataForUser4(tran, pid);
                    tran.Commit();
                    return result;
                }
            }
        }

        public bool GtsDepositPokemon4(MySqlTransaction tran, GtsRecord4 record)
        {
            if (record.Data.Length != 236) throw new FormatException("pkm data must be 236 bytes.");
            if (record.TrainerName.RawData.Length != 16) throw new FormatException("Trainer name must be 16 bytes.");
            // note that IsTraded being true in the record is not an error condition
            // since it might have use later on. You should check for this in the upload handler.

            long count = (long)tran.ExecuteScalar("SELECT Count(*) FROM GtsPokemon4 WHERE pid = @pid",
                new MySqlParameter("@pid", record.PID));

            if (count > 0)
            {
                // This player already has a pokemon in the system.
                // we can possibly allow multiples under some future conditions
                return false;
            }

            tran.ExecuteNonQuery("INSERT INTO GtsPokemon4 " +
                "(Data, Species, Gender, Level, RequestedSpecies, RequestedGender, " +
                "RequestedMinLevel, RequestedMaxLevel, Unknown1, TrainerGender, " +
                "Unknown2, TimeDeposited, TimeExchanged, pid, TrainerName, TrainerOT, " +
                "TrainerCountry, TrainerRegion, TrainerClass, IsExchanged, TrainerVersion, " +
                "TrainerLanguage) " +
                "VALUES (@Data, @Species, @Gender, @Level, @RequestedSpecies, " +
                "@RequestedGender, @RequestedMinLevel, @RequestedMaxLevel, @Unknown1, " +
                "@TrainerGender, @Unknown2, @TimeDeposited, @TimeExchanged, @pid, " +
                "@TrainerName, @TrainerOT, @TrainerCountry, @TrainerRegion, @TrainerClass, " +
                "@IsExchanged, @TrainerVersion, @TrainerLanguage)",
                ParamsFromRecord4(record));

            return true;
        }

        public override bool GtsDepositPokemon4(GtsRecord4 record)
        {
            if (record.Data.Length != 236) throw new FormatException("pkm data must be 236 bytes.");
            if (record.TrainerName.RawData.Length != 16) throw new FormatException("Trainer name must be 16 bytes.");
            // note that IsTraded being true in the record is not an error condition
            // since it might have use later on. You should check for this in the upload handler.

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    if (!GtsDepositPokemon4(tran, record))
                    {
                        tran.Rollback();
                        return false;
                    }

                    tran.Commit();
                    return true;
                }
            }
        }

        public ulong GtsGetDepositId4(int pid, MySqlTransaction tran)
        {
            object o = tran.ExecuteScalar("SELECT id FROM GtsPokemon4 WHERE pid = @pid " +
                "ORDER BY IsExchanged DESC, TimeExchanged, TimeDeposited LIMIT 1",
                new MySqlParameter("@pid", pid));
            if (o == null || o == DBNull.Value) return 0;
            return Convert.ToUInt64(o);
        }

        public bool GtsDeletePokemon4(MySqlTransaction tran, int pid)
        {
            ulong pkmnId = GtsGetDepositId4(pid, tran);
            if (pkmnId == 0) return false;

            tran.ExecuteNonQuery("DELETE FROM GtsPokemon4 WHERE id = @id",
                new MySqlParameter("@id", pkmnId));
            return true;
        }

        public override bool GtsDeletePokemon4(int pid)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    if (!GtsDeletePokemon4(tran, pid))
                    {
                        tran.Rollback();
                        return false;
                    }

                    tran.Commit();
                    return true;
                }
            }
        }

        public override bool GtsTradePokemon4(int pidSrc, int pidDest)
        {
            // not needed yet.
            return false;
        }

        public override bool GtsTradePokemon4(GtsRecord4 upload, GtsRecord4 result)
        {
            GtsRecord4 traded = upload.Clone();
            traded.FlagTraded(result);

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    GtsRecord4 resultOrig = GtsDataForUser4(tran, result.PID);
                    if (resultOrig == null || resultOrig != result)
                    {
                        // looks like the pokemon was ninja'd between the Exchange and Exchange_finish
                        tran.Rollback();
                        return false;
                    }

                    if (!GtsDeletePokemon4(tran, result.PID))
                    {
                        tran.Rollback();
                        return false;
                    }

                    if (!GtsDepositPokemon4(tran, traded))
                    {
                        tran.Rollback();
                        return false;
                    }

                    tran.Commit();
                    return true;
                }
            }
        }

        public override GtsRecord4[] GtsSearch4(int pid, ushort species, Genders gender, byte minLevel, byte maxLevel, byte country, int count)
        {
            using (MySqlConnection db = CreateConnection())
            {
                List<MySqlParameter> _params = new List<MySqlParameter>();
                String where = "WHERE pid != @pid AND IsExchanged = 0";
                _params.Add(new MySqlParameter("@pid", pid));

                if (species > 0)
                {
                    where += " AND Species = @species";
                    _params.Add(new MySqlParameter("@species", species));
                }

                if (gender != Genders.Either)
                {
                    where += " AND Gender = @gender";
                    _params.Add(new MySqlParameter("@gender", (byte)gender));
                }

                if (minLevel > 0 && maxLevel > 0)
                {
                    where += " AND Level BETWEEN @min_level AND @max_level";
                    _params.Add(new MySqlParameter("@min_level", minLevel));
                    _params.Add(new MySqlParameter("@max_level", maxLevel));
                }
                else if (minLevel > 0)
                {
                    where += " AND Level >= @min_level";
                    _params.Add(new MySqlParameter("@min_level", minLevel));
                }
                else if (maxLevel > 0)
                {
                    where += " AND Level <= @max_level";
                    _params.Add(new MySqlParameter("@max_level", maxLevel));
                }

                if (country > 0)
                {
                    where += " AND TrainerCountry = @country";
                    _params.Add(new MySqlParameter("@country", country));
                }

                String limit = "";
                if (count > 0)
                {
                    _params.Add(new MySqlParameter("@count", count));
                    limit = " LIMIT @count";
                }

                db.Open();
                // todo: sort me in creative ways
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT Data, Species, Gender, Level, " +
                    "RequestedSpecies, RequestedGender, RequestedMinLevel, RequestedMaxLevel, " +
                    "Unknown1, TrainerGender, Unknown2, TimeDeposited, TimeExchanged, pid, " +
                    "TrainerName, TrainerOT, TrainerCountry, TrainerRegion, TrainerClass, " +
                    "IsExchanged, TrainerVersion, TrainerLanguage FROM GtsPokemon4 " + where +
                    " ORDER BY TimeDeposited DESC" + limit,
                    _params.ToArray());

                List<GtsRecord4> records;
                if (count > 0) records = new List<GtsRecord4>(count);
                else records = new List<GtsRecord4>();

                while (reader.Read())
                {
                    records.Add(Record4FromReader(reader));
                }

                return records.ToArray();
            }
        }

        private static GtsRecord4 Record4FromReader(MySqlDataReader reader)
        {
            GtsRecord4 result = new GtsRecord4();

            byte[] data = new byte[236];
            reader.GetBytes(0, 0, data, 0, 236);
            result.Data = data;
            data = null;

            result.Species = reader.GetUInt16(1);
            result.Gender = (Genders)reader.GetByte(2);
            result.Level = reader.GetByte(3);
            result.RequestedSpecies = reader.GetUInt16(4);
            result.RequestedGender = (Genders)reader.GetByte(5);
            result.RequestedMinLevel = reader.GetByte(6);
            result.RequestedMaxLevel = reader.GetByte(7);
            result.Unknown1 = reader.GetByte(8);
            result.TrainerGender = (TrainerGenders)reader.GetByte(9);
            result.Unknown2 = reader.GetByte(10);
            if (reader.IsDBNull(11)) result.TimeDeposited = null;
            else result.TimeDeposited = reader.GetDateTime(11);
            if (reader.IsDBNull(12)) result.TimeExchanged = null;
            else result.TimeExchanged = reader.GetDateTime(12);
            result.PID = reader.GetInt32(13);

            data = new byte[16];
            reader.GetBytes(14, 0, data, 0, 16);
            result.TrainerName = new EncodedString4(data);
            data = null;

            result.TrainerOT = reader.GetUInt16(15);
            result.TrainerCountry = reader.GetByte(16);
            result.TrainerRegion = reader.GetByte(17);
            result.TrainerClass = reader.GetByte(18);
            result.IsExchanged = reader.GetByte(19);
            result.TrainerVersion = reader.GetByte(20);
            result.TrainerLanguage = reader.GetByte(21);

            return result;
        }

        private static MySqlParameter[] ParamsFromRecord4(GtsRecord4 record)
        {
            MySqlParameter[] result = new MySqlParameter[22];

            result[0] = new MySqlParameter("@Data", record.Data);
            result[1] = new MySqlParameter("@Species", record.Species);
            result[2] = new MySqlParameter("@Gender", (byte)record.Gender);
            result[3] = new MySqlParameter("@Level", record.Level);
            result[4] = new MySqlParameter("@RequestedSpecies", record.RequestedSpecies);
            result[5] = new MySqlParameter("@RequestedGender", (byte)record.RequestedGender);
            result[6] = new MySqlParameter("@RequestedMinLevel", record.RequestedMinLevel);
            result[7] = new MySqlParameter("@RequestedMaxLevel", record.RequestedMaxLevel);
            result[8] = new MySqlParameter("@Unknown1", record.Unknown1);
            result[9] = new MySqlParameter("@TrainerGender", (byte)record.TrainerGender);
            result[10] = new MySqlParameter("@Unknown2", record.Unknown2);
            result[11] = new MySqlParameter("@TimeDeposited", record.TimeDeposited);
            result[12] = new MySqlParameter("@TimeExchanged", record.TimeExchanged);
            result[13] = new MySqlParameter("@pid", record.PID);
            result[14] = new MySqlParameter("@TrainerName", record.TrainerName.RawData);
            result[15] = new MySqlParameter("@TrainerOT", record.TrainerOT);
            result[16] = new MySqlParameter("@TrainerCountry", record.TrainerCountry);
            result[17] = new MySqlParameter("@TrainerRegion", record.TrainerRegion);
            result[18] = new MySqlParameter("@TrainerClass", record.TrainerClass);
            result[19] = new MySqlParameter("@IsExchanged", record.IsExchanged);
            result[20] = new MySqlParameter("@TrainerVersion", record.TrainerVersion);
            result[21] = new MySqlParameter("@TrainerLanguage", record.TrainerLanguage);

            return result;
        }

        public override int GtsAvailablePokemon4()
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                return (int)(long)db.ExecuteScalar("SELECT Count(*) FROM GtsPokemon4 WHERE IsExchanged = 0");
            }
        }

        public override void GtsLogTrade4(GtsRecord4 record, DateTime ? timeWithdrawn, int ? partner_pid)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    GtsLogTrade4(tran, record, timeWithdrawn, partner_pid);
                    tran.Commit();
                }
            }
        }

        public void GtsLogTrade4(MySqlTransaction tran, GtsRecord4 record, DateTime? timeWithdrawn, int ? partner_pid)
        {
            if (record.Data.Length != 236) throw new FormatException("pkm data must be 236 bytes.");
            if (record.TrainerName.RawData.Length != 16) throw new FormatException("Trainer name must be 16 bytes.");
            // note that IsTraded being true in the record is not an error condition
            // since it might have use later on. You should check for this in the upload handler.

            int trade_id = (int)tran.ExecuteScalar("SELECT id FROM GtsPokemon4 WHERE pid = @pid",
                new MySqlParameter("@pid", record.PID));

            // when calling delete.asp, the partner pid can't be told from the request alone,
            // so obtain it from the database instead.
            if (record.IsExchanged != 0)
                partner_pid = (int ?)tran.ExecuteScalar("SELECT partner_pid FROM GtsHistory4 " +
                    "WHERE trade_id = @trade_id AND IsExchanged = 0", new MySqlParameter("@trade_id", trade_id));

            MySqlParameter[] _params = ParamsFromRecord4(record);
            MySqlParameter[] _params2 = new MySqlParameter[25];
            Array.Copy(_params, _params2, 22);
            _params2[22] = new MySqlParameter("@TimeWithdrawn", timeWithdrawn);
            _params2[23] = new MySqlParameter("@trade_id", trade_id);
            _params2[24] = new MySqlParameter("@partner_pid", partner_pid);

            tran.ExecuteNonQuery("INSERT INTO GtsHistory4 " +
                "(Data, Species, Gender, Level, RequestedSpecies, RequestedGender, " +
                "RequestedMinLevel, RequestedMaxLevel, Unknown1, TrainerGender, " +
                "Unknown2, TimeDeposited, TimeExchanged, pid, TrainerName, TrainerOT, " +
                "TrainerCountry, TrainerRegion, TrainerClass, IsExchanged, TrainerVersion, " +
                "TrainerLanguage, TimeWithdrawn, trade_id, partner_pid) " +
                "VALUES (@Data, @Species, @Gender, @Level, @RequestedSpecies, " +
                "@RequestedGender, @RequestedMinLevel, @RequestedMaxLevel, @Unknown1, " +
                "@TrainerGender, @Unknown2, @TimeDeposited, @TimeExchanged, @pid, " +
                "@TrainerName, @TrainerOT, @TrainerCountry, @TrainerRegion, @TrainerClass, " +
                "@IsExchanged, @TrainerVersion, @TrainerLanguage, @TimeWithdrawn, " +
                "@trade_id, @partner_pid)",
                _params2);
        }
        #endregion

        #region Battle Tower 4
        public override ulong BattleTowerUpdateRecord4(BattleTowerRecord4 record)
        {
            if (record.BattlesWon > 7) throw new ArgumentException("Battles won can not be greater than 7.");

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    ulong result = BattleTowerUpdateRecord4(tran, record);
                    tran.Commit();
                    return result;
                }
            }
        }

        private ulong BattleTowerUpdateRecord4(MySqlTransaction tran, BattleTowerRecord4 record)
        {
            if (record.BattlesWon > 7) throw new ArgumentException("Battles won can not be greater than 7.");

            // Does this player already have a record in this room?
            // Also get primary key if it does. (We need it for updating party)
            //
            // The official server doesn't seem to ever replace existing
            // records. This worked fine for them, but we don't have nearly
            // as many active players, so doing this will cause too many
            // duplicates. Instead, we require the trainers in a given room to
            // be unique by replacing their old record with a new one.
            ulong pkey = FindBattleTowerRecord4(tran, record, false);

            if (pkey != 0)
            {
                // If the player already has a record, move everyone below it up one position
                // (effectively removing this record from the ordering)

                // todo: In the case that the player's rank hasn't changed,
                // we can optimize this and the next down to a single BETWEEN
                // query.
                // This does require retrieving their old rank from the db.
                tran.ExecuteNonQuery("SELECT @old_rank = Rank, @old_position = Position " +
                    "FROM GtsBattleTower4 WHERE pid = @pid AND RoomNum = @room); " +
                    "UPDATE GtsBattleTower4 SET Position = Position - 1 " +
                    "WHERE RoomNum = @room AND Rank = @old_rank AND Position > @old_position",
                    new MySqlParameter("@pid", record.PID),
                    new MySqlParameter("@room", record.RoomNum));
            }

            uint position = (uint)(7 - record.BattlesWon);

            // Shift down all the players in the player's new rank by one.
            tran.ExecuteNonQuery("UPDATE GtsBattleTower4 SET Position = Position + 1 " +
                "WHERE RoomNum = @room AND Rank = @rank AND Position >= @position",
                new MySqlParameter("@room", record.RoomNum),
                new MySqlParameter("@rank", record.Rank),
                new MySqlParameter("@position", position));

            object lastPosition = tran.ExecuteScalar("SELECT MAX(Position) " +
                "FROM GtsBattleTower4 WHERE RoomNum = @room AND Rank = @rank",
                new MySqlParameter("@room", record.RoomNum),
                new MySqlParameter("@rank", record.Rank));

            // If the room has fewer than 7 trainers, insert this one at the
            // end but don't leave any gaps in the numbering.
            if (lastPosition is DBNull)
                position = 0;
            else
                position = Math.Min(position, (uint)lastPosition + 1);

            // Update the actual record
            if (pkey != 0)
            {
                List<MySqlParameter> _params = ParamsFromBattleTowerRecord4(record, false);
                _params.Add(new MySqlParameter("@position", position));
                _params.Add(new MySqlParameter("@id", pkey));

                tran.ExecuteNonQuery("UPDATE GtsBattleTower4 SET pid = @pid, Name = @name, " +
                    "Version = @version, Language = @language, Country = @country, " +
                    "Region = @region, TrainerID = @trainer_id, " +
                    "PhraseLeader = @phrase_leader, Gender = @gender, " +
                    "Unknown2 = @unknown2, PhraseChallenged = @phrase_challenged, " +
                    "PhraseWon = @phrase_won, PhraseLost = @phrase_lost, " +
                    "Unknown3 = @unknown3, " +
                    "Unknown5 = @unknown5, ParseVersion = 1, Rank = @rank, " +
                    "BattlesWon = @battles_won, Position = @position, " +
                    "TimeUpdated = UTC_TIMESTAMP() WHERE id = @id",
                    _params.ToArray());

                UpdateBattleTowerPokemon4(tran, record.Party[0], pkey, 0);
                UpdateBattleTowerPokemon4(tran, record.Party[1], pkey, 1);
                UpdateBattleTowerPokemon4(tran, record.Party[2], pkey, 2);
            }
            else
            {
                List<MySqlParameter> _params = ParamsFromBattleTowerRecord4(record, false);
                _params.Add(new MySqlParameter("@position", position));

                pkey = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO GtsBattleTower4 " +
                    "(pid, Name, Version, Language, Country, Region, TrainerID, " +
                    "PhraseLeader, Gender, Unknown2, PhraseChallenged, PhraseWon, " +
                    "PhraseLost, Unknown3, Unknown5, ParseVersion, " +
                    "Rank, RoomNum, BattlesWon, Position, TimeAdded, TimeUpdated) VALUES " +
                    "(@pid, @name, @version, @language, @country, @region, @trainer_id, " +
                    "@phrase_leader, @gender, @unknown2, @phrase_challenged, @phrase_won, " +
                    "@phrase_lost, @unknown3, @unknown5, 1, " +
                    "@rank, @room, @battles_won, @position, UTC_TIMESTAMP(), UTC_TIMESTAMP()); " +
                    "SELECT LAST_INSERT_ID()",
                    _params.ToArray()));

                InsertBattleTowerPokemon4(tran, record.Party[0], pkey, 0);
                InsertBattleTowerPokemon4(tran, record.Party[1], pkey, 1);
                InsertBattleTowerPokemon4(tran, record.Party[2], pkey, 2);
            }

            return pkey;
        }

        private void InsertBattleTowerPokemon4(MySqlTransaction tran, BattleTowerPokemon4 pokemon, ulong partyId, byte slot)
        {
            List<MySqlParameter> _params = ParamsFromBattleTowerPokemon4(pokemon);
            _params.Add(new MySqlParameter("@id", partyId));
            _params.Add(new MySqlParameter("@slot", slot));

            tran.ExecuteNonQuery("INSERT INTO GtsBattleTowerPokemon4 " +
                "(party_id, Slot, Species, HeldItem, Move1, Move2, Move3, Move4, TrainerID, " +
                "Personality, IVs, EVs, Unknown1, Language, Ability, Happiness, Nickname) VALUES " +
                "(@id, @slot, @species, @held_item, @move1, @move2, @move3, @move4, @trainer_id, " +
                "@personality, @ivs, @evs, @unknown1, @language, @ability, @happiness, @nickname)",
                _params.ToArray());
        }

        private void UpdateBattleTowerPokemon4(MySqlTransaction tran, BattleTowerPokemon4 pokemon, ulong partyId, byte slot)
        {
            List<MySqlParameter> _params = ParamsFromBattleTowerPokemon4(pokemon);
            _params.Add(new MySqlParameter("@id", partyId));
            _params.Add(new MySqlParameter("@slot", slot));

            tran.ExecuteNonQuery("UPDATE GtsBattleTowerPokemon4 SET Species = @species, " +
                "HeldItem = @held_item, Move1 = @move1, Move2 = @move2, Move3 = @move3, " +
                "Move4 = @move4, TrainerID = @trainer_id, Personality = @personality, " +
                "IVs = @ivs, EVs = @evs, Unknown1 = @unknown1, Language = @language, " +
                "Ability = @ability, Happiness = @happiness, Nickname = @nickname " +
                "WHERE party_id = @id AND Slot = @slot",
                _params.ToArray());
        }

        private List<MySqlParameter> ParamsFromBattleTowerRecord4(BattleTowerRecord4 record, bool leader)
        {
            List<MySqlParameter> result = new List<MySqlParameter>(15);
            result.Add(new MySqlParameter("@pid", record.PID));
            result.Add(new MySqlParameter("@name", record.Profile.Name.RawData));
            result.Add(new MySqlParameter("@version", (byte)record.Profile.Version));
            result.Add(new MySqlParameter("@language", (byte)record.Profile.Language));
            result.Add(new MySqlParameter("@country", record.Profile.Country));
            result.Add(new MySqlParameter("@region", record.Profile.Region));
            result.Add(new MySqlParameter("@trainer_id", record.Profile.OT));
            result.Add(new MySqlParameter("@phrase_leader", record.Profile.PhraseLeader.Data));
            result.Add(new MySqlParameter("@gender", record.Profile.Gender));
            result.Add(new MySqlParameter("@unknown2", record.Profile.Unknown));
            result.Add(new MySqlParameter("@rank", record.Rank));
            result.Add(new MySqlParameter("@room", record.RoomNum));
            if (!leader)
            {
                result.Add(new MySqlParameter("@phrase_challenged", record.PhraseChallenged.Data));
                result.Add(new MySqlParameter("@phrase_won", record.PhraseWon.Data));
                result.Add(new MySqlParameter("@phrase_lost", record.PhraseLost.Data));
                result.Add(new MySqlParameter("@unknown3", record.Unknown3));
                result.Add(new MySqlParameter("@unknown5", record.Unknown5));
                result.Add(new MySqlParameter("@battles_won", record.BattlesWon));
            }
            return result;
        }

        private List<MySqlParameter> ParamsFromBattleTowerPokemon4(BattleTowerPokemon4 pokemon)
        {
            List<MySqlParameter> result = new List<MySqlParameter>(15);
            result.Add(new MySqlParameter("@species", pokemon.Species));
            result.Add(new MySqlParameter("@held_item", pokemon.HeldItem));
            result.Add(new MySqlParameter("@move1", pokemon.Moveset[0]));
            result.Add(new MySqlParameter("@move2", pokemon.Moveset[1]));
            result.Add(new MySqlParameter("@move3", pokemon.Moveset[2]));
            result.Add(new MySqlParameter("@move4", pokemon.Moveset[3]));
            result.Add(new MySqlParameter("@trainer_id", pokemon.OT));
            result.Add(new MySqlParameter("@personality", pokemon.Personality));
            result.Add(new MySqlParameter("@ivs", pokemon.IVs));
            result.Add(new MySqlParameter("@evs", pokemon.EVs));
            result.Add(new MySqlParameter("@unknown1", pokemon.Unknown1));
            result.Add(new MySqlParameter("@language", (byte)pokemon.Language));
            result.Add(new MySqlParameter("@ability", pokemon.Ability));
            result.Add(new MySqlParameter("@happiness", pokemon.Happiness));
            result.Add(new MySqlParameter("@nickname", pokemon.Nickname.RawData));
            return result;
        }

        public override ulong BattleTowerAddLeader4(BattleTowerRecord4 record)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    ulong result = BattleTowerAddLeader4(tran, record);
                    tran.Commit();
                    return result;
                }
            }
        }

        private ulong BattleTowerAddLeader4(MySqlTransaction tran, BattleTowerRecord4 record)
        {
            ulong pkey = FindBattleTowerRecord4(tran, record, true);

            // Update the actual record
            if (pkey != 0)
            {
                List<MySqlParameter> _params = ParamsFromBattleTowerRecord4(record, true);
                _params.Add(new MySqlParameter("@id", pkey));

                tran.ExecuteNonQuery("UPDATE GtsBattleTowerLeaders4 SET " +
                    "pid = @pid, Name = @name, Version = @version, " +
                    "Language = @language, Country = @country, Region = @region, " +
                    "TrainerID = @trainer_id, " +
                    "PhraseLeader = @phrase_leader, Gender = @gender, Unknown2 = @unknown2, " +
                    "ParseVersion = 1, Rank = @rank, " +
                    "TimeUpdated = UTC_TIMESTAMP() WHERE id = @id",
                    _params.ToArray());
            }
            else
            {
                List<MySqlParameter> _params = ParamsFromBattleTowerRecord4(record, true);

                pkey = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO " +
                    "GtsBattleTowerLeaders4 " +
                    "(pid, Name, Version, Language, Country, Region, TrainerID, " +
                    "PhraseLeader, Gender, Unknown2, ParseVersion, Rank, " +
                    "RoomNum, TimeAdded, TimeUpdated) VALUES " +
                    "(@pid, @name, @version, @language, @country, @region, @trainer_id, " +
                    "@phrase_leader, @gender, @unknown2, 1, @rank, " +
                    "@room, UTC_TIMESTAMP(), UTC_TIMESTAMP()); " +
                    "SELECT LAST_INSERT_ID()",
                    _params.ToArray()));
            }

            return pkey;
        }

        /// <summary>
        /// Tries to find an existing database record for the provided player
        /// record. The match must be found in the same rank and room number.
        /// </summary>
        /// <param name="tran"></param>
        /// <param name="record"></param>
        /// <param name="leader">If true, look up against the Leaders table.
        /// Otherwise looks up against the opponents table.</param>
        /// <returns>The match's primary key or 0 if no match is found
        /// </returns>
        private ulong FindBattleTowerRecord4(MySqlTransaction tran, BattleTowerRecord4 record, bool leader)
        {
            String tblName = leader ? "GtsBattleTowerLeaders4" : "GtsBattleTower4";

            // If PID is missing, this is restored data.
            // We assume the original server took care of matching existing
            // records, so we don't allow it to match here.
            if (record.PID == 0) return 0;

            // Match normally.
            object oPkey = tran.ExecuteScalar("SELECT id FROM " + tblName +
                " WHERE pid = @pid AND RoomNum = @room AND Rank = @rank",
                new MySqlParameter("@pid", record.PID),
                new MySqlParameter("@rank", record.Rank),
                new MySqlParameter("@room", record.RoomNum));

            if (oPkey == null)
            {
                // PID isn't found. Try to match one of Pikachu025's saved
                // records based on unchanging properties of the savegame.
                oPkey = tran.ExecuteScalar("SELECT id FROM " + tblName +
                    " WHERE pid = 0 AND RoomNum = @room AND Rank = @rank " +
                    "AND Name = @name AND Version = @version " +
                    "AND Language = @language AND TrainerID = @trainer_id",
                    new MySqlParameter("@rank", record.Rank),
                    new MySqlParameter("@room", record.RoomNum),
                    new MySqlParameter("@name", record.Profile.Name),
                    new MySqlParameter("@version", (byte)record.Profile.Version),
                    new MySqlParameter("@language", (byte)record.Profile.Language),
                    new MySqlParameter("@trainer_id", record.Profile.OT)
                );
            }

            // Don't need to worry about DBNull since the column is non-null.
            return (ulong)(oPkey ?? 0UL);
        }

        public override BattleTowerRecord4[] BattleTowerGetOpponents4(int pid, byte rank, byte roomNum)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    List<BattleTowerRecord4> records = new List<BattleTowerRecord4>(7);
                    List<ulong> keys = new List<ulong>(7);
                    MySqlDataReader reader = (MySqlDataReader)tran.ExecuteReader(
                        "SELECT id, pid, Name, " +
                        "Version, Language, Country, Region, TrainerID, " +
                        "PhraseLeader, Gender, Unknown2, PhraseChallenged, " +
                        "PhraseWon, PhraseLost, Unknown3, Unknown5 FROM GtsBattleTower4 " +
                        "WHERE Rank = @rank AND RoomNum = @room AND pid != @pid " +
                        "ORDER BY Position LIMIT 7",
                        new MySqlParameter("@rank", rank),
                        new MySqlParameter("@room", roomNum),
                        new MySqlParameter("@pid", pid));
                    while (reader.Read())
                    {
                        BattleTowerRecord4 record = BattleTowerRecord4FromReader(reader);
                        record.Party = new BattleTowerPokemon4[3];
                        records.Add(record);
                        keys.Add(reader.GetUInt64(0));
                    }
                    reader.Close();

                    if (records.Count == 0) return new BattleTowerRecord4[0];

                    String inClause = String.Join(", ", keys.Select(i => i.ToString()).ToArray());
                    reader = (MySqlDataReader)tran.ExecuteReader("SELECT party_id, " +
                        "Slot, Species, HeldItem, Move1, Move2, Move3, Move4, " +
                        "TrainerID, Personality, IVs, EVs, Unknown1, Language, " +
                        "Ability, Happiness, Nickname FROM GtsBattleTowerPokemon4 " +
                        "WHERE party_id IN (" + inClause + ")");
                    while (reader.Read())
                    {
                        BattleTowerRecord4 record = records[keys.IndexOf(reader.GetUInt64(0))];
                        record.Party[reader.GetByte(1)] = BattleTowerPokemon4FromReader(reader);
                    }
                    reader.Close();

                    tran.Commit();
                    return Enumerable.Reverse(records).ToArray();
                }
            }
        }

        private BattleTowerRecord4 BattleTowerRecord4FromReader(MySqlDataReader reader)
        {
            // todo: Stop using ordinals everywhere.
            BattleTowerRecord4 result = new BattleTowerRecord4();
            result.PID = reader.GetInt32(1);

            if (reader.FieldCount > 11) result.PhraseChallenged = new TrendyPhrase4(reader.GetByteArray(11, 8));
            if (reader.FieldCount > 12) result.PhraseWon = new TrendyPhrase4(reader.GetByteArray(12, 8));
            if (reader.FieldCount > 13) result.PhraseLost = new TrendyPhrase4(reader.GetByteArray(13, 8));
            if (reader.FieldCount > 14) result.Unknown3 = reader.GetUInt16(14);
            if (reader.FieldCount > 15) result.Unknown5 = reader.GetUInt64(15);

            BattleTowerProfile4 profile = new BattleTowerProfile4();
            profile.Name = new EncodedString4(reader.GetByteArray(2, 16));
            profile.Version = (Versions)reader.GetByte(3);
            profile.Language = (Languages)reader.GetByte(4);
            profile.Country = reader.GetByte(5);
            profile.Region = reader.GetByte(6);
            profile.OT = reader.GetUInt32(7);
            profile.PhraseLeader = new TrendyPhrase4(reader.GetByteArray(8, 8));
            profile.Gender = reader.GetByte(9);
            profile.Unknown = reader.GetByte(10);

            result.Profile = profile;
            return result;
        }

        private BattleTowerPokemon4 BattleTowerPokemon4FromReader(MySqlDataReader reader)
        {
            BattleTowerPokemon4 result = new BattleTowerPokemon4();
            result.Species = reader.GetUInt16(2);
            result.HeldItem = reader.GetUInt16(3);
            result.Moveset = new ushort[4];
            result.Moveset[0] = reader.GetUInt16(4);
            result.Moveset[1] = reader.GetUInt16(5);
            result.Moveset[2] = reader.GetUInt16(6);
            result.Moveset[3] = reader.GetUInt16(7);
            result.OT = reader.GetUInt32(8);
            result.Personality = reader.GetUInt32(9);
            result.IVs = reader.GetUInt32(10);
            result.EVs = reader.GetByteArray(11, 6);
            result.Unknown1 = reader.GetByte(12);
            result.Language = (Languages)reader.GetByte(13);
            result.Ability = reader.GetByte(14);
            result.Happiness = reader.GetByte(15);
            result.Nickname = new EncodedString4(reader.GetByteArray(16, 22));

            return result;
        }

        public override BattleTowerProfile4[] BattleTowerGetLeaders4(byte rank, byte roomNum)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    List<BattleTowerProfile4> profiles = new List<BattleTowerProfile4>(30);
                    MySqlDataReader reader = (MySqlDataReader)tran.ExecuteReader(
                        "SELECT id, pid, Name, " +
                        "Version, Language, Country, Region, TrainerID, " +
                        "PhraseLeader, Gender, Unknown2 FROM GtsBattleTowerLeaders4 " +
                        "WHERE Rank = @rank AND RoomNum = @room " +
                        "ORDER BY TimeUpdated DESC, id LIMIT 30",
                        new MySqlParameter("@rank", rank),
                        new MySqlParameter("@room", roomNum));
                    while (reader.Read())
                    {
                        profiles.Add(BattleTowerRecord4FromReader(reader).Profile);
                    }
                    reader.Close();

                    tran.Commit();
                    return profiles.ToArray();
                }
            }
        }
        #endregion

        #region Other Gamestats 4
        public override bool GamestatsSetProfile4(TrainerProfile4 profile)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    bool result = GamestatsSetProfile4(tran, profile);
                    tran.Commit();
                    return result;
                }
            }
        }

        public bool GamestatsSetProfile4(MySqlTransaction tran, TrainerProfile4 profile)
        {
            if (profile.Data.Length != 100) throw new FormatException("Profile data must be 100 bytes.");

            long exists = (long)tran.ExecuteScalar("SELECT EXISTS(SELECT * FROM GtsProfiles4 WHERE pid = @pid)", new MySqlParameter("@pid", profile.PID));

            MySqlParameter[] _params = new MySqlParameter[]{
                new MySqlParameter("@pid", profile.PID),
                new MySqlParameter("@data", profile.Data),
                new MySqlParameter("@version", (byte)profile.Version),
                new MySqlParameter("@language", (byte)profile.Language),
                new MySqlParameter("@country", profile.Country),
                new MySqlParameter("@region", profile.Region),
                new MySqlParameter("@ot", profile.OT),
                new MySqlParameter("@name", profile.Name.RawData)
            };

            if (exists != 0)
            {
                return tran.ExecuteNonQuery("UPDATE GtsProfiles4 SET Data = @data, " +
                    "Version = @version, Language = @language, Country = @country, " +
                    "Region = @region, OT = @ot, Name = @name, ParseVersion = 1, " +
                    "TimeUpdated = UTC_TIMESTAMP() " +
                    "WHERE pid = @pid", _params) > 0;
            }
            else
            {
                return tran.ExecuteNonQuery("INSERT INTO GtsProfiles4 " +
                    "(pid, Data, Version, Language, Country, Region, OT, Name, " +
                    "ParseVersion, TimeAdded, TimeUpdated) VALUES " +
                    "(@pid, @data, @version, @language, @country, @region, @ot, " +
                    "@name, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP())", _params) > 0;
            }
        }
        #endregion

        #region GTS 5
        public GtsRecord5 GtsDataForUser5(MySqlTransaction tran, int pid)
        {
            MySqlDataReader reader = (MySqlDataReader)tran.ExecuteReader("SELECT Data, Unknown0, " +
                "Species, Gender, Level, " +
                "RequestedSpecies, RequestedGender, RequestedMinLevel, RequestedMaxLevel, " +
                "Unknown1, TrainerGender, Unknown2, TimeDeposited, TimeExchanged, pid, " +
                "TrainerOT, TrainerName, TrainerCountry, TrainerRegion, TrainerClass, " +
                "IsExchanged, TrainerVersion, TrainerLanguage, TrainerBadges, TrainerUnityTower " +
                "FROM GtsPokemon5 WHERE pid = @pid",
                new MySqlParameter("@pid", pid));

            if (!reader.Read())
            {
                reader.Close();
                return null;
            }
            GtsRecord5 result = Record5FromReader(reader);
#if DEBUG
            AssertHelper.Equals(result.PID, pid);
#endif
            reader.Close();
            return result;
        }

        public override GtsRecord5 GtsDataForUser5(int pid)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    GtsRecord5 result = GtsDataForUser5(tran, pid);
                    tran.Commit();
                    return result;
                }
            }
        }

        public bool GtsDepositPokemon5(MySqlTransaction tran, GtsRecord5 record)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (record.Data.Length != 220) throw new FormatException("pkm data must be 220 bytes.");
            if (record.Unknown0.Length != 16) throw new FormatException("pkm padding must be 16 bytes.");
            if (record.TrainerName.RawData.Length != 16) throw new FormatException("Trainer name must be 16 bytes.");
            // note that IsTraded being true in the record is not an error condition
            // since it might have use later on. You should check for this in the upload handler.

            long count = (long)tran.ExecuteScalar("SELECT Count(*) FROM GtsPokemon5 WHERE pid = @pid",
                new MySqlParameter("@pid", record.PID));

            if (count > 0)
            {
                // This player already has a pokemon in the system.
                // we can possibly allow multiples under some future conditions
                return false;
            }

            tran.ExecuteNonQuery("INSERT INTO GtsPokemon5 " +
                "(Data, Unknown0, Species, Gender, Level, RequestedSpecies, RequestedGender, " +
                "RequestedMinLevel, RequestedMaxLevel, Unknown1, TrainerGender, " +
                "Unknown2, TimeDeposited, TimeExchanged, pid, TrainerOT, TrainerName, " +
                "TrainerCountry, TrainerRegion, TrainerClass, IsExchanged, TrainerVersion, " +
                "TrainerLanguage, TrainerBadges, TrainerUnityTower) " +
                "VALUES (@Data, @Unknown0, @Species, @Gender, @Level, @RequestedSpecies, " +
                "@RequestedGender, @RequestedMinLevel, @RequestedMaxLevel, @Unknown1, " +
                "@TrainerGender, @Unknown2, @TimeDeposited, @TimeExchanged, @pid, " +
                "@TrainerOT, @TrainerName, @TrainerCountry, @TrainerRegion, @TrainerClass, " +
                "@IsExchanged, @TrainerVersion, @TrainerLanguage, @TrainerBadges, @TrainerUnityTower)",
                ParamsFromRecord5(record));

            return true;
        }

        public override bool GtsDepositPokemon5(GtsRecord5 record)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (record.Data.Length != 220) throw new FormatException("pkm data must be 220 bytes.");
            if (record.Unknown0.Length != 16) throw new FormatException("pkm padding must be 16 bytes.");
            if (record.TrainerName.RawData.Length != 16) throw new FormatException("Trainer name must be 16 bytes.");
            // note that IsTraded being true in the record is not an error condition
            // since it might have use later on. You should check for this in the upload handler.

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    if (!GtsDepositPokemon5(tran, record))
                    {
                        tran.Rollback();
                        return false;
                    }

                    tran.Commit();
                    return true;
                }
            }
        }

        public ulong GtsGetDepositId5(int pid, MySqlTransaction tran)
        {
            object o = tran.ExecuteScalar("SELECT id FROM GtsPokemon5 WHERE pid = @pid " +
                "ORDER BY IsExchanged DESC, TimeExchanged, TimeDeposited LIMIT 1",
                new MySqlParameter("@pid", pid));
            if (o == null || o == DBNull.Value) return 0;
            return Convert.ToUInt64(o);
        }

        public bool GtsDeletePokemon5(MySqlTransaction tran, int pid)
        {
            ulong pkmnId = GtsGetDepositId5(pid, tran);
            if (pkmnId == 0) return false;

            tran.ExecuteNonQuery("DELETE FROM GtsPokemon5 WHERE id = @id",
                new MySqlParameter("@id", pkmnId));
            return true;
        }

        public override bool GtsDeletePokemon5(int pid)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    if (!GtsDeletePokemon5(tran, pid))
                    {
                        tran.Rollback();
                        return false;
                    }

                    tran.Commit();
                    return true;
                }
            }
        }

        public override bool GtsTradePokemon5(int pidSrc, int pidDest)
        {
            // not needed yet.
            return false;
        }

        public override bool GtsTradePokemon5(GtsRecord5 upload, GtsRecord5 result)
        {
            GtsRecord5 traded = upload.Clone();
            traded.FlagTraded(result);

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    GtsRecord5 resultOrig = GtsDataForUser5(tran, result.PID);
                    if (resultOrig == null || resultOrig != result)
                    {
                        // looks like the pokemon was ninja'd between the Exchange and Exchange_finish
                        tran.Rollback();
                        return false;
                    }

                    if (!GtsDeletePokemon5(tran, result.PID))
                    {
                        tran.Rollback();
                        return false;
                    }

                    if (!GtsDepositPokemon5(tran, traded))
                    {
                        tran.Rollback();
                        return false;
                    }

                    tran.Commit();
                    return true;
                }
            }
        }

        public override GtsRecord5[] GtsSearch5(int pid, ushort species, Genders gender, byte minLevel, byte maxLevel, byte country, int count)
        {
            using (MySqlConnection db = CreateConnection())
            {
                List<MySqlParameter> _params = new List<MySqlParameter>();
                String where = "WHERE pid != @pid AND IsExchanged = 0";
                _params.Add(new MySqlParameter("@pid", pid));

                if (species > 0)
                {
                    where += " AND Species = @species";
                    _params.Add(new MySqlParameter("@species", species));
                }

                if (gender != Genders.Either)
                {
                    where += " AND Gender = @gender";
                    _params.Add(new MySqlParameter("@gender", (byte)gender));
                }

                if (minLevel > 0 && maxLevel > 0)
                {
                    where += " AND Level BETWEEN @min_level AND @max_level";
                    _params.Add(new MySqlParameter("@min_level", minLevel));
                    _params.Add(new MySqlParameter("@max_level", maxLevel));
                }
                else if (minLevel > 0)
                {
                    where += " AND Level >= @min_level";
                    _params.Add(new MySqlParameter("@min_level", minLevel));
                }
                else if (maxLevel > 0)
                {
                    where += " AND Level <= @max_level";
                    _params.Add(new MySqlParameter("@max_level", maxLevel));
                }

                if (country > 0)
                {
                    where += " AND TrainerCountry = @country";
                    _params.Add(new MySqlParameter("@country", country));
                }

                String limit = "";
                if (count > 0)
                {
                    _params.Add(new MySqlParameter("@count", count));
                    limit = " LIMIT @count";
                }

                db.Open();
                // todo: sort me in creative ways
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT Data, Unknown0, " +
                    "Species, Gender, Level, " +
                    "RequestedSpecies, RequestedGender, RequestedMinLevel, RequestedMaxLevel, " +
                    "Unknown1, TrainerGender, Unknown2, TimeDeposited, TimeExchanged, pid, " +
                    "TrainerOT, TrainerName, TrainerCountry, TrainerRegion, TrainerClass, " +
                    "IsExchanged, TrainerVersion, TrainerLanguage, TrainerBadges, TrainerUnityTower " +
                    "FROM GtsPokemon5 " + where +
                    " ORDER BY TimeDeposited DESC" + limit,
                    _params.ToArray());

                List<GtsRecord5> records;
                if (count > 0) records = new List<GtsRecord5>(count);
                else records = new List<GtsRecord5>();

                while (reader.Read())
                {
                    records.Add(Record5FromReader(reader));
                }

                return records.ToArray();
            }
        }

        private static GtsRecord5 Record5FromReader(MySqlDataReader reader)
        {
            GtsRecord5 result = new GtsRecord5();

            byte[] data = new byte[220];
            reader.GetBytes(0, 0, data, 0, 220);
            result.Data = data;
            data = null;

            data = new byte[16];
            reader.GetBytes(1, 0, data, 0, 16);
            result.Unknown0 = data;
            data = null;

            result.Species = reader.GetUInt16(2);
            result.Gender = (Genders)reader.GetByte(3);
            result.Level = reader.GetByte(4);
            result.RequestedSpecies = reader.GetUInt16(5);
            result.RequestedGender = (Genders)reader.GetByte(6);
            result.RequestedMinLevel = reader.GetByte(7);
            result.RequestedMaxLevel = reader.GetByte(8);
            result.Unknown1 = reader.GetByte(9);
            result.TrainerGender = (TrainerGenders)reader.GetByte(10);
            result.Unknown2 = reader.GetByte(11);
            if (reader.IsDBNull(12)) result.TimeDeposited = null;
            else result.TimeDeposited = reader.GetDateTime(12);
            if (reader.IsDBNull(13)) result.TimeExchanged = null;
            else result.TimeExchanged = reader.GetDateTime(13);
            result.PID = reader.GetInt32(14);
            result.TrainerOT = reader.GetUInt32(15);

            data = new byte[16];
            reader.GetBytes(16, 0, data, 0, 16);
            result.TrainerName = new EncodedString5(data);
            data = null;

            result.TrainerCountry = reader.GetByte(17);
            result.TrainerRegion = reader.GetByte(18);
            result.TrainerClass = reader.GetByte(19);
            result.IsExchanged = reader.GetByte(20);
            result.TrainerVersion = reader.GetByte(21);
            result.TrainerLanguage = reader.GetByte(22);
            result.TrainerBadges = reader.GetByte(23);
            result.TrainerUnityTower = reader.GetByte(24);

            return result;
        }

        private static MySqlParameter[] ParamsFromRecord5(GtsRecord5 record)
        {
            MySqlParameter[] result = new MySqlParameter[25];

            result[0] = new MySqlParameter("@Data", record.Data);
            result[1] = new MySqlParameter("@Unknown0", record.Unknown0);
            result[2] = new MySqlParameter("@Species", record.Species);
            result[3] = new MySqlParameter("@Gender", (byte)record.Gender);
            result[4] = new MySqlParameter("@Level", record.Level);
            result[5] = new MySqlParameter("@RequestedSpecies", record.RequestedSpecies);
            result[6] = new MySqlParameter("@RequestedGender", (byte)record.RequestedGender);
            result[7] = new MySqlParameter("@RequestedMinLevel", record.RequestedMinLevel);
            result[8] = new MySqlParameter("@RequestedMaxLevel", record.RequestedMaxLevel);
            result[9] = new MySqlParameter("@Unknown1", record.Unknown1);
            result[10] = new MySqlParameter("@TrainerGender", (byte)record.TrainerGender);
            result[11] = new MySqlParameter("@Unknown2", record.Unknown2);
            result[12] = new MySqlParameter("@TimeDeposited", record.TimeDeposited);
            result[13] = new MySqlParameter("@TimeExchanged", record.TimeExchanged);
            result[14] = new MySqlParameter("@pid", record.PID);
            result[15] = new MySqlParameter("@TrainerOT", record.TrainerOT);
            result[16] = new MySqlParameter("@TrainerName", record.TrainerName.RawData);
            result[17] = new MySqlParameter("@TrainerCountry", record.TrainerCountry);
            result[18] = new MySqlParameter("@TrainerRegion", record.TrainerRegion);
            result[19] = new MySqlParameter("@TrainerClass", record.TrainerClass);
            result[20] = new MySqlParameter("@IsExchanged", record.IsExchanged);
            result[21] = new MySqlParameter("@TrainerVersion", record.TrainerVersion);
            result[22] = new MySqlParameter("@TrainerLanguage", record.TrainerLanguage);
            result[23] = new MySqlParameter("@TrainerBadges", record.TrainerBadges);
            result[24] = new MySqlParameter("@TrainerUnityTower", record.TrainerUnityTower);

            return result;
        }

        public override int GtsAvailablePokemon5()
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                return (int)(long)db.ExecuteScalar("SELECT Count(*) FROM GtsPokemon5 WHERE IsExchanged = 0");
            }
        }

        public override void GtsLogTrade5(GtsRecord5 record, DateTime ? timeWithdrawn, int ? partner_pid)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    GtsLogTrade5(tran, record, timeWithdrawn, partner_pid);
                    tran.Commit();
                }
            }
        }

        public void GtsLogTrade5(MySqlTransaction tran, GtsRecord5 record, DateTime ? timeWithdrawn, int ? partner_pid)
        {
            // todo: Bring these out into a ValidateRecord5 method
            if (record == null) throw new ArgumentNullException("record");
            if (record.Data.Length != 220) throw new FormatException("pkm data must be 220 bytes.");
            if (record.Unknown0.Length != 16) throw new FormatException("pkm padding must be 16 bytes.");
            if (record.TrainerName.RawData.Length != 16) throw new FormatException("Trainer name must be 16 bytes.");
            // note that IsTraded being true in the record is not an error condition
            // since it might have use later on. You should check for this in the upload handler.

            int trade_id = (int)tran.ExecuteScalar("SELECT id FROM GtsPokemon5 WHERE pid = @pid",
                new MySqlParameter("@pid", record.PID));

            // when calling delete.asp, the partner pid can't be told from the request alone,
            // so obtain it from the database instead.
            if (record.IsExchanged != 0)
                partner_pid = (int ?)tran.ExecuteScalar("SELECT partner_pid FROM GtsHistory5 " +
                    "WHERE trade_id = @trade_id AND IsExchanged = 0", new MySqlParameter("@trade_id", trade_id));

            MySqlParameter[] _params = ParamsFromRecord5(record);
            MySqlParameter[] _params2 = new MySqlParameter[28];
            Array.Copy(_params, _params2, 25);
            _params2[25] = new MySqlParameter("@TimeWithdrawn", timeWithdrawn);
            _params2[26] = new MySqlParameter("@trade_id", trade_id);
            _params2[27] = new MySqlParameter("@partner_pid", partner_pid);

            tran.ExecuteNonQuery("INSERT INTO GtsHistory5 " +
                "(Data, Unknown0, Species, Gender, Level, RequestedSpecies, RequestedGender, " +
                "RequestedMinLevel, RequestedMaxLevel, Unknown1, TrainerGender, " +
                "Unknown2, TimeDeposited, TimeExchanged, pid, TrainerOT, TrainerName, " +
                "TrainerCountry, TrainerRegion, TrainerClass, IsExchanged, TrainerVersion, " +
                "TrainerLanguage, TrainerBadges, TrainerUnityTower, TimeWithdrawn, " +
                "trade_id, partner_pid) " +
                "VALUES (@Data, @Unknown0, @Species, @Gender, @Level, @RequestedSpecies, " +
                "@RequestedGender, @RequestedMinLevel, @RequestedMaxLevel, @Unknown1, " +
                "@TrainerGender, @Unknown2, @TimeDeposited, @TimeExchanged, @pid, " +
                "@TrainerOT, @TrainerName, @TrainerCountry, @TrainerRegion, @TrainerClass, " +
                "@IsExchanged, @TrainerVersion, @TrainerLanguage, @TrainerBadges, " +
                "@TrainerUnityTower, @TimeWithdrawn, @trade_id, @partner_pid)",
                _params2);
        }

        #endregion

        #region Battle Subway 5
        public override ulong BattleSubwayUpdateRecord5(BattleSubwayRecord5 record)
        {
            if (record.BattlesWon > 7) throw new ArgumentException("Battles won can not be greater than 7.");

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    ulong result = BattleSubwayUpdateRecord5(tran, record);
                    tran.Commit();
                    return result;
                }
            }
        }

        private ulong BattleSubwayUpdateRecord5(MySqlTransaction tran, BattleSubwayRecord5 record)
        {
            if (record.BattlesWon > 7) throw new ArgumentException("Battles won can not be greater than 7.");

            // Does this player already have a record in this room?
            // Also get primary key if it does. (We need it for updating party)
            ulong pkey = FindBattleSubwayRecord5(tran, record, false);

            if (pkey != 0)
            {
                // If the player already has a record, move everyone below it up one position
                // (effectively removing this record from the ordering)

                // todo: In the case that the player's rank hasn't changed,
                // we can optimize this and the next down to a single BETWEEN
                // query.
                // This does require retrieving their old rank from the db.
                tran.ExecuteNonQuery("SELECT @old_rank = Rank, @old_position = Position " +
                    "FROM GtsBattleSubway5 WHERE pid = @pid AND RoomNum = @room); " +
                    "UPDATE GtsBattleSubway5 SET Position = Position - 1 " +
                    "WHERE RoomNum = @room AND Rank = @old_rank AND Position > @old_position",
                    new MySqlParameter("@pid", record.PID),
                    new MySqlParameter("@room", record.RoomNum));
            }

            uint position = (uint)(7 - record.BattlesWon);

            // Shift down all the players in the player's new rank by one.
            tran.ExecuteNonQuery("UPDATE GtsBattleSubway5 SET Position = Position + 1 " +
                "WHERE RoomNum = @room AND Rank = @rank AND Position >= @position",
                new MySqlParameter("@room", record.RoomNum),
                new MySqlParameter("@rank", record.Rank),
                new MySqlParameter("@position", position));

            object lastPosition = tran.ExecuteScalar("SELECT MAX(Position) " +
                "FROM GtsBattleSubway5 WHERE RoomNum = @room AND Rank = @rank",
                new MySqlParameter("@room", record.RoomNum),
                new MySqlParameter("@rank", record.Rank));

            // If the room has fewer than 7 trainers, insert this one at the
            // end but don't leave any gaps in the numbering.
            if (lastPosition is DBNull)
                position = 0;
            else
                position = Math.Min(position, (uint)lastPosition + 1);

            // Update the actual record
            if (pkey != 0)
            {
                List<MySqlParameter> _params = ParamsFromBattleSubwayRecord5(record, false);
                _params.Add(new MySqlParameter("@position", position));
                _params.Add(new MySqlParameter("@id", pkey));

                tran.ExecuteNonQuery("UPDATE GtsBattleSubway5 SET pid = @pid, Name = @name, " +
                    "Version = @version, Language = @language, Country = @country, " +
                    "Region = @region, TrainerID = @trainer_id, " +
                    "PhraseLeader = @phrase_leader, Gender = @gender, " +
                    "Unknown2 = @unknown2, PhraseChallenged = @phrase_challenged, " +
                    "PhraseWon = @phrase_won, PhraseLost = @phrase_lost, Unknown3 = @unknown3, " +
                    "Unknown4 = @unknown4, Unknown5 = @unknown5, ParseVersion = 1, Rank = @rank, " +
                    "BattlesWon = @battles_won, Position = @position, " +
                    "TimeUpdated = UTC_TIMESTAMP() WHERE id = @id",
                    _params.ToArray());

                UpdateBattleSubwayPokemon5(tran, record.Party[0], pkey, 0);
                UpdateBattleSubwayPokemon5(tran, record.Party[1], pkey, 1);
                UpdateBattleSubwayPokemon5(tran, record.Party[2], pkey, 2);
            }
            else
            {
                List<MySqlParameter> _params = ParamsFromBattleSubwayRecord5(record, false);
                _params.Add(new MySqlParameter("@position", position));

                pkey = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO GtsBattleSubway5 " +
                    "(pid, Name, Version, Language, Country, Region, TrainerID, " +
                    "PhraseLeader, Gender, Unknown2, PhraseChallenged, PhraseWon, " +
                    "PhraseLost, Unknown3, Unknown4, Unknown5, ParseVersion, " +
                    "Rank, RoomNum, BattlesWon, Position, TimeAdded, TimeUpdated) VALUES " +
                    "(@pid, @name, @version, @language, @country, @region, @trainer_id, " +
                    "@phrase_leader, @gender, @unknown2, @phrase_challenged, @phrase_won, " +
                    "@phrase_lost, @unknown3, @unknown4, @unknown5, 1, " +
                    "@rank, @room, @battles_won, @position, UTC_TIMESTAMP(), UTC_TIMESTAMP()); " +
                    "SELECT LAST_INSERT_ID()",
                    _params.ToArray()));

                InsertBattleSubwayPokemon5(tran, record.Party[0], pkey, 0);
                InsertBattleSubwayPokemon5(tran, record.Party[1], pkey, 1);
                InsertBattleSubwayPokemon5(tran, record.Party[2], pkey, 2);
            }

            return pkey;
        }

        private void InsertBattleSubwayPokemon5(MySqlTransaction tran, BattleSubwayPokemon5 pokemon, ulong partyId, byte slot)
        {
            List<MySqlParameter> _params = ParamsFromBattleSubwayPokemon5(pokemon);
            _params.Add(new MySqlParameter("@id", partyId));
            _params.Add(new MySqlParameter("@slot", slot));

            tran.ExecuteNonQuery("INSERT INTO GtsBattleSubwayPokemon5 " +
                "(party_id, Slot, Species, HeldItem, Move1, Move2, Move3, Move4, TrainerID, " +
                "Personality, IVs, EVs, Unknown1, Language, Ability, Happiness, " +
                "Nickname, Unknown2) VALUES " +
                "(@id, @slot, @species, @held_item, @move1, @move2, @move3, @move4, @trainer_id, " +
                "@personality, @ivs, @evs, @unknown1, @language, @ability, @happiness, " +
                "@nickname, @unknown2)",
                _params.ToArray());
        }

        private void UpdateBattleSubwayPokemon5(MySqlTransaction tran, BattleSubwayPokemon5 pokemon, ulong partyId, byte slot)
        {
            List<MySqlParameter> _params = ParamsFromBattleSubwayPokemon5(pokemon);
            _params.Add(new MySqlParameter("@id", partyId));
            _params.Add(new MySqlParameter("@slot", slot));

            tran.ExecuteNonQuery("UPDATE GtsBattleSubwayPokemon5 SET Species = @species, " +
                "HeldItem = @held_item, Move1 = @move1, Move2 = @move2, Move3 = @move3, " +
                "Move4 = @move4, TrainerID = @trainer_id, Personality = @personality, " +
                "IVs = @ivs, EVs = @evs, Unknown1 = @unknown1, Language = @language, " +
                "Ability = @ability, Happiness = @happiness, Nickname = @nickname, " +
                "Unknown2 = @unknown2 " +
                "WHERE party_id = @id AND Slot = @slot",
                _params.ToArray());
        }

        private List<MySqlParameter> ParamsFromBattleSubwayRecord5(BattleSubwayRecord5 record, bool leader)
        {
            List<MySqlParameter> result = new List<MySqlParameter>(15);
            result.Add(new MySqlParameter("@pid", record.PID));
            result.Add(new MySqlParameter("@name", record.Profile.Name.RawData));
            result.Add(new MySqlParameter("@version", (byte)record.Profile.Version));
            result.Add(new MySqlParameter("@language", (byte)record.Profile.Language));
            result.Add(new MySqlParameter("@country", record.Profile.Country));
            result.Add(new MySqlParameter("@region", record.Profile.Region));
            result.Add(new MySqlParameter("@trainer_id", record.Profile.OT));
            result.Add(new MySqlParameter("@phrase_leader", record.Profile.PhraseLeader.Data));
            result.Add(new MySqlParameter("@gender", record.Profile.Gender));
            result.Add(new MySqlParameter("@unknown2", record.Profile.Unknown));
            result.Add(new MySqlParameter("@rank", record.Rank));
            result.Add(new MySqlParameter("@room", record.RoomNum));
            if (!leader)
            {
                result.Add(new MySqlParameter("@phrase_challenged", record.PhraseChallenged.Data));
                result.Add(new MySqlParameter("@phrase_won", record.PhraseWon.Data));
                result.Add(new MySqlParameter("@phrase_lost", record.PhraseLost.Data));
                result.Add(new MySqlParameter("@unknown3", record.Unknown3));
                result.Add(new MySqlParameter("@unknown4", record.Unknown4 ?? new byte[5]));
                result.Add(new MySqlParameter("@unknown5", record.Unknown5));
                result.Add(new MySqlParameter("@battles_won", record.BattlesWon));
            }
            return result;
        }

        private List<MySqlParameter> ParamsFromBattleSubwayPokemon5(BattleSubwayPokemon5 pokemon)
        {
            List<MySqlParameter> result = new List<MySqlParameter>(15);
            result.Add(new MySqlParameter("@species", pokemon.Species));
            result.Add(new MySqlParameter("@held_item", pokemon.HeldItem));
            result.Add(new MySqlParameter("@move1", pokemon.Moveset[0]));
            result.Add(new MySqlParameter("@move2", pokemon.Moveset[1]));
            result.Add(new MySqlParameter("@move3", pokemon.Moveset[2]));
            result.Add(new MySqlParameter("@move4", pokemon.Moveset[3]));
            result.Add(new MySqlParameter("@trainer_id", pokemon.OT));
            result.Add(new MySqlParameter("@personality", pokemon.Personality));
            result.Add(new MySqlParameter("@ivs", pokemon.IVs));
            result.Add(new MySqlParameter("@evs", pokemon.EVs));
            result.Add(new MySqlParameter("@unknown1", pokemon.Unknown1));
            result.Add(new MySqlParameter("@language", (byte)pokemon.Language));
            result.Add(new MySqlParameter("@ability", pokemon.Ability));
            result.Add(new MySqlParameter("@happiness", pokemon.Happiness));
            result.Add(new MySqlParameter("@nickname", pokemon.Nickname.RawData));
            result.Add(new MySqlParameter("@unknown2", pokemon.Unknown2));
            return result;
        }

        public override ulong BattleSubwayAddLeader5(BattleSubwayRecord5 record)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    ulong result = BattleSubwayAddLeader5(tran, record);
                    tran.Commit();
                    return result;
                }
            }
        }

        private ulong BattleSubwayAddLeader5(MySqlTransaction tran, BattleSubwayRecord5 record)
        {
            ulong pkey = FindBattleSubwayRecord5(tran, record, true);

            // Update the actual record
            if (pkey != 0)
            {
                List<MySqlParameter> _params = ParamsFromBattleSubwayRecord5(record, true);
                _params.Add(new MySqlParameter("@id", pkey));

                tran.ExecuteNonQuery("UPDATE GtsBattleSubwayLeaders5 SET " +
                    "pid = @pid, Name = @name, Version = @version, " +
                    "Language = @language, Country = @country, Region = @region, " +
                    "TrainerID = @trainer_id, " +
                    "PhraseLeader = @phrase_leader, Gender = @gender, Unknown2 = @unknown2, " +
                    "ParseVersion = 1, Rank = @rank, " +
                    "TimeUpdated = UTC_TIMESTAMP() WHERE id = @id",
                    _params.ToArray());
            }
            else
            {
                List<MySqlParameter> _params = ParamsFromBattleSubwayRecord5(record, true);

                pkey = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO " +
                    "GtsBattleSubwayLeaders5 " +
                    "(pid, Name, Version, Language, Country, Region, TrainerID, " +
                    "PhraseLeader, Gender, Unknown2, ParseVersion, Rank, " +
                    "RoomNum, TimeAdded, TimeUpdated) VALUES " +
                    "(@pid, @name, @version, @language, @country, @region, @trainer_id, " +
                    "@phrase_leader, @gender, @unknown2, 1, @rank, " +
                    "@room, UTC_TIMESTAMP(), UTC_TIMESTAMP()); " +
                    "SELECT LAST_INSERT_ID()",
                    _params.ToArray()));
            }

            return pkey;
        }

        /// <summary>
        /// Tries to find an existing database record for the provided player
        /// record. The match must be found in the same rank and room number.
        /// </summary>
        /// <param name="tran"></param>
        /// <param name="record"></param>
        /// <param name="leader">If true, look up against the Leaders table.
        /// Otherwise looks up against the opponents table.</param>
        /// <returns>The match's primary key or 0 if no match is found
        /// </returns>
        private ulong FindBattleSubwayRecord5(MySqlTransaction tran, BattleSubwayRecord5 record, bool leader)
        {
            String tblName = leader ? "GtsBattleSubwayLeaders5" : "GtsBattleSubway5";

            // If PID is missing, this is restored data.
            // We assume the original server took care of matching existing
            // records, so we don't allow it to match here.
            if (record.PID == 0) return 0;

            // Match normally.
            object oPkey = tran.ExecuteScalar("SELECT id FROM " + tblName +
                " WHERE pid = @pid AND RoomNum = @room AND Rank = @rank", // Only require rank to match if this is the leaderboard.
                new MySqlParameter("@pid", record.PID),
                new MySqlParameter("@rank", record.Rank),
                new MySqlParameter("@room", record.RoomNum));

            if (oPkey == null)
            {
                // PID isn't found. Try to match one of Pikachu025's saved
                // records based on unchanging properties of the savegame.
                oPkey = tran.ExecuteScalar("SELECT id FROM " + tblName +
                    " WHERE pid = 0 AND RoomNum = @room AND Rank = @rank " + // Only require rank to match if this is the leaderboard.
                    "AND Name = @name AND Version = @version " +
                    "AND Language = @language AND TrainerID = @trainer_id",
                    new MySqlParameter("@rank", record.Rank),
                    new MySqlParameter("@room", record.RoomNum),
                    new MySqlParameter("@name", record.Profile.Name),
                    new MySqlParameter("@version", (byte)record.Profile.Version),
                    new MySqlParameter("@language", (byte)record.Profile.Language),
                    new MySqlParameter("@trainer_id", record.Profile.OT)
                );
            }

            // Don't need to worry about DBNull since the column is non-null.
            return (ulong)(oPkey ?? 0UL);
        }

        public override BattleSubwayRecord5[] BattleSubwayGetOpponents5(int pid, byte rank, byte roomNum)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    List<BattleSubwayRecord5> records = new List<BattleSubwayRecord5>(7);
                    List<ulong> keys = new List<ulong>(7);
                    MySqlDataReader reader = (MySqlDataReader)tran.ExecuteReader(
                        "SELECT id, pid, Name, " +
                        "Version, Language, Country, Region, TrainerID, " +
                        "PhraseLeader, Gender, Unknown2, PhraseChallenged, " +
                        "PhraseWon, PhraseLost, Unknown3, Unknown4, Unknown5 " +
                        "FROM GtsBattleSubway5 " +
                        "WHERE Rank = @rank AND RoomNum = @room AND pid != @pid " +
                        "ORDER BY Position LIMIT 7",
                        new MySqlParameter("@rank", rank),
                        new MySqlParameter("@room", roomNum),
                        new MySqlParameter("@pid", pid));
                    while (reader.Read())
                    {
                        BattleSubwayRecord5 record = BattleSubwayRecord5FromReader(reader);
                        record.Party = new BattleSubwayPokemon5[3];
                        records.Add(record);
                        keys.Add(reader.GetUInt64(0));
                    }
                    reader.Close();

                    if (records.Count == 0) return new BattleSubwayRecord5[0];

                    String inClause = String.Join(", ", keys.Select(i => i.ToString()).ToArray());
                    reader = (MySqlDataReader)tran.ExecuteReader("SELECT party_id, " +
                        "Slot, Species, HeldItem, Move1, Move2, Move3, Move4, " +
                        "TrainerID, Personality, IVs, EVs, Unknown1, Language, " +
                        "Ability, Happiness, Nickname, Unknown2 FROM GtsBattleSubwayPokemon5 " +
                        "WHERE party_id IN (" + inClause + ")");
                    while (reader.Read())
                    {
                        BattleSubwayRecord5 record = records[keys.IndexOf(reader.GetUInt64(0))];
                        record.Party[reader.GetByte(1)] = BattleSubwayPokemon5FromReader(reader);
                    }
                    reader.Close();

                    tran.Commit();
                    return Enumerable.Reverse(records).ToArray();
                }
            }
        }

        private BattleSubwayRecord5 BattleSubwayRecord5FromReader(MySqlDataReader reader)
        {
            // todo: Stop using ordinals everywhere.
            BattleSubwayRecord5 result = new BattleSubwayRecord5();
            result.PID = reader.GetInt32(1);
            // this is unsustainable. What happens if I add columns to Leaders?
            if (reader.FieldCount > 11) result.PhraseChallenged = new TrendyPhrase5(reader.GetByteArray(11, 8));
            if (reader.FieldCount > 12) result.PhraseWon = new TrendyPhrase5(reader.GetByteArray(12, 8));
            if (reader.FieldCount > 13) result.PhraseLost = new TrendyPhrase5(reader.GetByteArray(13, 8));
            if (reader.FieldCount > 14) result.Unknown3 = reader.GetUInt16(14);
            if (reader.FieldCount > 15 && !reader.IsDBNull(15)) result.Unknown4 = reader.GetByteArray(15, 5);
            if (reader.FieldCount > 16) result.Unknown5 = reader.GetUInt64(16);

            BattleSubwayProfile5 profile = new BattleSubwayProfile5();
            profile.Name = new EncodedString5(reader.GetByteArray(2, 16));
            profile.Version = (Versions)reader.GetByte(3);
            profile.Language = (Languages)reader.GetByte(4);
            profile.Country = reader.GetByte(5);
            profile.Region = reader.GetByte(6);
            profile.OT = reader.GetUInt32(7);
            profile.PhraseLeader = new TrendyPhrase5(reader.GetByteArray(8, 8));
            profile.Gender = reader.GetByte(9);
            profile.Unknown = reader.GetByte(10);

            result.Profile = profile;
            return result;
        }

        private BattleSubwayPokemon5 BattleSubwayPokemon5FromReader(MySqlDataReader reader)
        {
            BattleSubwayPokemon5 result = new BattleSubwayPokemon5();
            result.Species = reader.GetUInt16(2);
            result.HeldItem = reader.GetUInt16(3);
            result.Moveset = new ushort[4];
            result.Moveset[0] = reader.GetUInt16(4);
            result.Moveset[1] = reader.GetUInt16(5);
            result.Moveset[2] = reader.GetUInt16(6);
            result.Moveset[3] = reader.GetUInt16(7);
            result.OT = reader.GetUInt32(8);
            result.Personality = reader.GetUInt32(9);
            result.IVs = reader.GetUInt32(10);
            result.EVs = reader.GetByteArray(11, 6);
            result.Unknown1 = reader.GetByte(12);
            result.Language = (Languages)reader.GetByte(13);
            result.Ability = reader.GetByte(14);
            result.Happiness = reader.GetByte(15);
            result.Nickname = new EncodedString5(reader.GetByteArray(16, 22));
            result.Unknown2 = reader.GetUInt32(17);

            return result;
        }

        public override BattleSubwayProfile5[] BattleSubwayGetLeaders5(byte rank, byte roomNum)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    List<BattleSubwayProfile5> profiles = new List<BattleSubwayProfile5>(30);
                    MySqlDataReader reader = (MySqlDataReader)tran.ExecuteReader(
                        "SELECT id, pid, Name, " +
                        "Version, Language, Country, Region, TrainerID, " +
                        "PhraseLeader, Gender, Unknown2 FROM GtsBattleSubwayLeaders5 " +
                        "WHERE Rank = @rank AND RoomNum = @room " +
                        "ORDER BY TimeUpdated DESC, id LIMIT 30",
                        new MySqlParameter("@rank", rank),
                        new MySqlParameter("@room", roomNum));
                    while (reader.Read())
                    {
                        profiles.Add(BattleSubwayRecord5FromReader(reader).Profile);
                    }
                    reader.Close();

                    tran.Commit();
                    return profiles.ToArray();
                }
            }
        }
        #endregion

        #region Other Gamestats 5
        public override bool GamestatsSetProfile5(TrainerProfile5 profile)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    bool result = GamestatsSetProfile5(tran, profile);
                    tran.Commit();
                    return result;
                }
            }
        }

        public bool GamestatsSetProfile5(MySqlTransaction tran, TrainerProfile5 profile)
        {
            if (profile.Data.Length != 100) throw new FormatException("Profile data must be 100 bytes.");

            long exists = (long)tran.ExecuteScalar("SELECT EXISTS(SELECT * FROM GtsProfiles5 WHERE pid = @pid)", new MySqlParameter("@pid", profile.PID));

            MySqlParameter[] _params = new MySqlParameter[]{
                new MySqlParameter("@pid", profile.PID),
                new MySqlParameter("@data", profile.Data),
                new MySqlParameter("@version", (byte)profile.Version),
                new MySqlParameter("@language", (byte)profile.Language),
                new MySqlParameter("@country", profile.Country),
                new MySqlParameter("@region", profile.Region),
                new MySqlParameter("@ot", profile.OT),
                new MySqlParameter("@name", profile.Name.RawData)
            };

            if (exists != 0)
            {
                return tran.ExecuteNonQuery("UPDATE GtsProfiles5 SET Data = @data, " +
                    "Version = @version, Language = @language, Country = @country, " +
                    "Region = @region, OT = @ot, Name = @name, ParseVersion = 1, " +
                    "TimeUpdated = UTC_TIMESTAMP() " +
                    "WHERE pid = @pid", _params) > 0;
            }
            else
            {
                return tran.ExecuteNonQuery("INSERT INTO GtsProfiles5 " +
                    "(pid, Data, Version, Language, Country, Region, OT, Name, " +
                    "ParseVersion, TimeAdded, TimeUpdated) VALUES " +
                    "(@pid, @data, @version, @language, @country, @region, @ot, " +
                    "@name, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP())", _params) > 0;
            }
        }
        #endregion

        #region Global Terminal 4
        public override ulong DressupUpload4(DressupRecord4 record)
        {
            if (record.Data.Length != 224) throw new ArgumentException("Dressup data must be 224 bytes.");
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    long exists = (long)tran.ExecuteScalar("SELECT EXISTS(SELECT * FROM TerminalDressup4 WHERE md5 = unhex(md5(@data)) AND Data = @data)", new MySqlParameter("@data", record.Data));
                    if (exists != 0) return 0;

                    if (record.SerialNumber == 0)
                    {
                        ulong serial = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO TerminalDressup4 (pid, " +
                            "Data, md5, TimeAdded, ParseVersion, Species) VALUES (@pid, @data, " +
                            "unhex(md5(@data)), UTC_TIMESTAMP(), 1, @species); SELECT LAST_INSERT_ID()",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@species", record.Species)));
                        tran.Commit();
                        return serial;
                    }
                    else
                    {
                        int rows = tran.ExecuteNonQuery("INSERT INTO TerminalDressup4 (pid, SerialNumber, " +
                            "Data, md5, TimeAdded, ParseVersion, Species) VALUES (@pid, @serial, @data, " +
                            "unhex(md5(@data)), UTC_TIMESTAMP(), 1, @species)",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@serial", record.SerialNumber),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@species", record.Species));
                        tran.Commit();

                        return rows > 0 ? record.SerialNumber : 0;
                    }
                }
            }
        }

        public override DressupRecord4[] DressupSearch4(ushort species, int count)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<DressupRecord4> results = new List<DressupRecord4>(count);
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT pid, " +
                    "SerialNumber, Data FROM TerminalDressup4 WHERE Species = @species " +
                    "ORDER BY TimeAdded DESC LIMIT @count", 
                    new MySqlParameter("@species", species), 
                    new MySqlParameter("@count", count));
                while (reader.Read())
                {
                    results.Add(Dressup4FromReader(reader));
                }

                reader.Close();
                db.Close();
                return results.ToArray();
            }
        }

        private DressupRecord4 Dressup4FromReader(MySqlDataReader reader)
        {
            byte[] data = new byte[224];
            reader.GetBytes(2, 0, data, 0, 224);

            return new DressupRecord4(reader.GetInt32(0), reader.GetUInt64(1), data);
        }

        public override ulong BoxUpload4(BoxRecord4 record)
        {
            if (record.Data.Length != 540) throw new ArgumentException("Box data must be 540 bytes.");
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    long exists = (long)tran.ExecuteScalar("SELECT EXISTS(SELECT * FROM TerminalBoxes4 WHERE md5 = unhex(md5(@data)) AND Data = @data)", new MySqlParameter("@data", record.Data));
                    if (exists != 0) return 0;

                    if (record.SerialNumber == 0)
                    {
                        ulong serial = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO TerminalBoxes4 (pid, " +
                            "Data, md5, TimeAdded, ParseVersion, Label) VALUES (@pid, @data, " +
                            "unhex(md5(@data)), UTC_TIMESTAMP(), 1, @label); SELECT LAST_INSERT_ID()",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@label", (int)record.Label)));
                        tran.Commit();
                        return serial;
                    }
                    else
                    {
                        int rows = tran.ExecuteNonQuery("INSERT INTO TerminalBoxes4 (pid, SerialNumber, " +
                            "Data, md5, TimeAdded, ParseVersion, Label) VALUES (@pid, @serial, @data, " +
                            "unhex(md5(@data)), UTC_TIMESTAMP(), 1, @label)",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@serial", record.SerialNumber),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@label", (int)record.Label));
                        tran.Commit();

                        return rows > 0 ? record.SerialNumber : 0;
                    }
                }
            }
        }

        public override BoxRecord4[] BoxSearch4(BoxLabels4 label, int count)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<BoxRecord4> results = new List<BoxRecord4>(count);
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT pid, " +
                    "Label, SerialNumber, Data FROM TerminalBoxes4 WHERE Label = @label " +
                    "ORDER BY TimeAdded DESC LIMIT @count",
                    new MySqlParameter("@label", (int)label),
                    new MySqlParameter("@count", count));
                while (reader.Read())
                {
                    results.Add(Box4FromReader(reader));
                }

                reader.Close();
                db.Close();
                return results.ToArray();
            }
        }

        private BoxRecord4 Box4FromReader(MySqlDataReader reader)
        {
            byte[] data = new byte[540];
            reader.GetBytes(3, 0, data, 0, 540);

            return new BoxRecord4(reader.GetInt32(0), (BoxLabels4)reader.GetInt32(1), reader.GetUInt64(2), data);
        }

        public override ulong BattleVideoUpload4(BattleVideoRecord4 record)
        {
            if (record.Data.Length != 7272) throw new ArgumentException();
            if (record.Header.Data.Length != 228) throw new ArgumentException();

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    long exists = (long)tran.ExecuteScalar("SELECT EXISTS(SELECT * " +
                        "FROM TerminalBattleVideos4 WHERE md5 = unhex(md5(CONCAT(@header, @data))) " +
                        "AND Data = @data AND Header = @header)", 
                        new MySqlParameter("@header", record.Header.Data), 
                        new MySqlParameter("@data", record.Data));
                    if (exists != 0) return 0;

                    if (record.SerialNumber == 0)
                    {
                        ulong key = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO TerminalBattleVideos4 " +
                            "(pid, Header, Data, md5, TimeAdded, ParseVersion, Streak, TrainerName, " +
                            "Metagame, Country, Region) " +
                            "VALUES (@pid, @header, @data, unhex(md5(CONCAT(@header, @data))), " +
                            "UTC_TIMESTAMP(), 1, @streak, @trainer, @metagame, @country, @region); " +
                            "SELECT LAST_INSERT_ID()",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@header", record.Header.Data),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@streak", record.Header.Streak),
                            new MySqlParameter("@trainer", record.Header.TrainerName),
                            new MySqlParameter("@metagame", (byte)record.Header.Metagame),
                            new MySqlParameter("@country", (byte)record.Header.Country),
                            new MySqlParameter("@region", (byte)record.Header.Region)
                            ));
                        ulong serial = BattleVideoHeader4.KeyToSerial(key);

                        tran.ExecuteNonQuery("UPDATE TerminalBattleVideos4 SET " +
                            "SerialNumber = @serial WHERE id = @key", 
                            new MySqlParameter("@serial", serial), 
                            new MySqlParameter("@key", key));

                        // todo: make a proc to insert both video and party.
                        InsertBattleVideoParty4(record.Header, key, tran);

                        tran.Commit();
                        return serial;
                    }
                    else
                    {
                        ulong key = BattleVideoHeader4.SerialToKey(record.SerialNumber);

                        int rows = tran.ExecuteNonQuery("INSERT INTO TerminalBattleVideos4 " +
                            "(id, pid, SerialNumber, Header, Data, md5, TimeAdded, " +
                            "ParseVersion, Streak, TrainerName, " +
                            "Metagame, Country, Region) " +
                            "VALUES (@key, @pid, @serial, @header, @data, " +
                            "unhex(md5(CONCAT(@header, @data))), " +
                            "UTC_TIMESTAMP(), 1, @streak, @trainer, @metagame, @country, @region)",
                            new MySqlParameter("@key", key),
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@serial", record.SerialNumber),
                            new MySqlParameter("@header", record.Header.Data),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@streak", record.Header.Streak),
                            new MySqlParameter("@trainer", record.Header.TrainerName),
                            new MySqlParameter("@metagame", (byte)record.Header.Metagame),
                            new MySqlParameter("@country", (byte)record.Header.Country),
                            new MySqlParameter("@region", (byte)record.Header.Region)
                            );

                        if (rows == 0) return 0;

                        InsertBattleVideoParty4(record.Header, key, tran);

                        tran.Commit();
                        return record.SerialNumber;
                    }
                }
            }
        }

        private void InsertBattleVideoParty4(BattleVideoHeader4 header, ulong key, MySqlTransaction tran)
        {
            MySqlCommand cmd = new MySqlCommand("INSERT INTO " +
            "TerminalBattleVideoPokemon4 (video_id, Slot, Species) VALUES " +
            "(@key, @slot, @species)", tran.Connection, tran);
            cmd.Parameters.Add("@key", MySqlDbType.UInt64).Value = key;
            cmd.Parameters.Add("@slot", MySqlDbType.UByte);
            cmd.Parameters.Add("@species", MySqlDbType.UInt16);

            ushort[] party = header.Party;
            for (byte x = 0; x < 12; x++)
            {
                ushort species = party[x];
                if (species == 0) continue;
                cmd.Parameters["@slot"].Value = x;
                cmd.Parameters["@species"].Value = species;
                cmd.ExecuteNonQuery();
            }
        }

        public override BattleVideoHeader4[] BattleVideoSearch4(ushort species, BattleVideoRankings4 ranking, BattleVideoMetagames4 metagame, byte country, byte region, int count)
        {
            using (MySqlConnection db = CreateConnection())
            {
                List<MySqlParameter> _params = new List<MySqlParameter>();
                String where = "";
                String sort = "";
                bool hasSearch = false;

                if (ranking == BattleVideoRankings4.None)
                {
                    if (species != 0xffff)
                    {
                        where += " WHERE EXISTS(SELECT * FROM TerminalBattleVideoPokemon4 " +
                            "WHERE video_id = TerminalBattleVideos4.id AND Species = @species)";
                        _params.Add(new MySqlParameter("@species", species));
                        hasSearch = true;
                    }

                    if (metagame == BattleVideoMetagames4.SearchColosseumSingleNoRestrictions)
                        metagame = BattleVideoMetagames4.ColosseumSingleNoRestrictions;
                    if (metagame == BattleVideoMetagames4.SearchColosseumDoubleNoRestrictions)
                        metagame = BattleVideoMetagames4.ColosseumDoubleNoRestrictions;

                    if (metagame == BattleVideoMetagames4.SearchColosseumSingleCupMatch)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame BETWEEN 1 AND 6";
                        hasSearch = true;
                    }
                    else if (metagame == BattleVideoMetagames4.SearchColosseumDoubleCupMatch)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame BETWEEN 8 AND 13";
                        hasSearch = true;
                    }
                    else if (metagame != BattleVideoMetagames4.SearchLatest30)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame = @metagame";
                        _params.Add(new MySqlParameter("@metagame", (byte)metagame));
                        hasSearch = true;
                    }

                    if (country != 0xff)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Country = @country";
                        _params.Add(new MySqlParameter("@country", country));
                        hasSearch = true;
                    }

                    if (region != 0xff)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Region = @region";
                        _params.Add(new MySqlParameter("@region", region));
                    }

                    sort = " ORDER BY TimeAdded DESC, id DESC";
                }
                else if (ranking == BattleVideoRankings4.Colosseum)
                {
                    // todo: sort by .. something.
                    where = " WHERE Metagame BETWEEN 0 AND 14";
                    sort = " ORDER BY Streak DESC, TimeAdded DESC, id DESC";
                }
                else if (ranking == BattleVideoRankings4.BattleFrontier)
                {
                    where = " WHERE NOT (Metagame BETWEEN 0 AND 14)";
                    sort = " ORDER BY Streak DESC, TimeAdded DESC, id DESC";
                }
                else
                {
                    sort = " ORDER BY TimeAdded DESC, id DESC";
                }

                _params.Add(new MySqlParameter("@count", count));

                db.Open();

                List<BattleVideoHeader4> results = new List<BattleVideoHeader4>(count);
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT pid, " +
                    "SerialNumber, Header FROM TerminalBattleVideos4" + where +
                    sort + " LIMIT @count",
                    _params.ToArray());
                while (reader.Read())
                {
                    results.Add(BattleVideoHeader4FromReader(reader));
                }

                reader.Close();
                db.Close();
                return results.ToArray();
            }
        }

        private BattleVideoHeader4 BattleVideoHeader4FromReader(MySqlDataReader reader)
        {
            byte[] data = new byte[228];
            reader.GetBytes(2, 0, data, 0, 228);

            return new BattleVideoHeader4(reader.GetInt32(0), reader.GetUInt64(1), data);
        }

        public override BattleVideoRecord4 BattleVideoGet4(ulong serial, bool incrementViews = false)
        {
            String update = incrementViews ? "UPDATE TerminalBattleVideos4 " +
                "SET Views = Views + 1 WHERE SerialNumber = @serial; "
                : "";

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader(update + "SELECT pid, " +
                    "SerialNumber, Header, Data FROM TerminalBattleVideos4 " +
                    "WHERE SerialNumber = @serial", 
                    new MySqlParameter("@serial", serial));

                if (reader.Read())
                    return BattleVideo4FromReader(reader);
                else return null;
            }
        }

        private BattleVideoRecord4 BattleVideo4FromReader(MySqlDataReader reader)
        {
            byte[] data = new byte[7272];
            reader.GetBytes(3, 0, data, 0, 7272);
            BattleVideoHeader4 header = BattleVideoHeader4FromReader(reader);

            return new BattleVideoRecord4(header.PID, header.SerialNumber, header, data);
        }

        public override bool BattleVideoFlagSaved4(ulong serial)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                int results = db.ExecuteNonQuery("UPDATE TerminalBattleVideos4 " +
                    "SET Saves = Saves + 1 WHERE SerialNumber = @serial", 
                    new MySqlParameter("@serial", serial));
                db.Close();

                return results > 0;
            }
        }

        public override ulong BattleVideoCount4()
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                return Convert.ToUInt64(db.ExecuteScalar("SELECT Count(*) FROM TerminalBattleVideos4"));
            }
        }
        #endregion

        #region Global Terminal 5
        public override ulong MusicalUpload5(MusicalRecord5 record)
        {
            if (record.Data.Length != 560) throw new ArgumentException();

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    long exists = (long)tran.ExecuteScalar("SELECT EXISTS(SELECT * " +
                        "FROM TerminalMusicals5 WHERE md5 = unhex(md5(@data)) " +
                        "AND Data = @data)",
                        new MySqlParameter("@data", record.Data));
                    if (exists != 0) return 0;

                    if (record.SerialNumber == 0)
                    {
                        ulong serial = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO TerminalMusicals5 " +
                            "(pid, Data, md5, TimeAdded, ParseVersion) " +
                            "VALUES (@pid, @data, unhex(md5(@data)), " +
                            "UTC_TIMESTAMP(), 1); " +
                            "SELECT LAST_INSERT_ID()",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@data", record.Data)
                            ));

                        // todo: make a proc to insert both musical and party.
                        InsertMusicalParticipants5(record, serial, tran);

                        tran.Commit();
                        return serial;
                    }
                    else
                    {
                        int rows = tran.ExecuteNonQuery("INSERT INTO TerminalMusicals5 " +
                            "(pid, SerialNumber, Data, md5, TimeAdded, ParseVersion) " +
                            "VALUES (@pid, @serial, @data, unhex(md5(@data)), " +
                            "UTC_TIMESTAMP(), 1)",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@serial", record.SerialNumber),
                            new MySqlParameter("@data", record.Data)
                            );

                        if (rows == 0) return 0;

                        InsertMusicalParticipants5(record, record.SerialNumber, tran);

                        tran.Commit();
                        return record.SerialNumber;
                    }
                }
            }
        }

        private void InsertMusicalParticipants5(MusicalRecord5 record, ulong SerialNumber, MySqlTransaction tran)
        {
            MySqlCommand cmd = new MySqlCommand("INSERT INTO " +
            "TerminalMusicalPokemon5 (musical_id, Slot, Species) VALUES " +
            "(@serial, @slot, @species)", tran.Connection, tran);
            cmd.Parameters.Add("@serial", MySqlDbType.UInt64).Value = SerialNumber;
            cmd.Parameters.Add("@slot", MySqlDbType.UByte);
            cmd.Parameters.Add("@species", MySqlDbType.UInt16);

            MusicalParticipant5[] participants = record.Participants;
            for (byte x = 0; x < 4; x++)
            {
                ushort species = participants[x].Species;
                if (species == 0) continue;
                cmd.Parameters["@slot"].Value = x;
                cmd.Parameters["@species"].Value = species;
                cmd.ExecuteNonQuery();
            }
        }

        public override MusicalRecord5[] MusicalSearch5(ushort species, int count)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<MusicalRecord5> results = new List<MusicalRecord5>(count);
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT pid, " +
                    "SerialNumber, Data FROM TerminalMusicals5 " +
                    "WHERE EXISTS(SELECT * FROM TerminalMusicalPokemon5 " +
                    "WHERE musical_id = TerminalMusicals5.SerialNumber AND Species = @species) " +
                    "ORDER BY TimeAdded DESC LIMIT @count",
                    new MySqlParameter("@species", species),
                    new MySqlParameter("@count", count));
                while (reader.Read())
                {
                    results.Add(Musical5FromReader(reader));
                }

                reader.Close();
                db.Close();
                return results.ToArray();
            }
        }

        private MusicalRecord5 Musical5FromReader(MySqlDataReader reader)
        {
            byte[] data = new byte[560];
            reader.GetBytes(2, 0, data, 0, 560);

            return new MusicalRecord5(reader.GetInt32(0), reader.GetUInt64(1), data);
        }

        public override ulong BattleVideoUpload5(BattleVideoRecord5 record)
        {
            if (record.Data.Length != 6112) throw new ArgumentException();
            if (record.Header.Data.Length != 196) throw new ArgumentException();

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                using (MySqlTransaction tran = db.BeginTransaction())
                {
                    long exists = (long)tran.ExecuteScalar("SELECT EXISTS(SELECT * " +
                        "FROM TerminalBattleVideos5 WHERE md5 = unhex(md5(CONCAT(@header, @data))) " +
                        "AND Data = @data AND Header = @header)",
                        new MySqlParameter("@header", record.Header.Data),
                        new MySqlParameter("@data", record.Data));
                    if (exists != 0) return 0;

                    if (record.SerialNumber == 0)
                    {
                        ulong key = Convert.ToUInt64(tran.ExecuteScalar("INSERT INTO TerminalBattleVideos5 " +
                            "(pid, Header, Data, md5, TimeAdded, ParseVersion, Streak, TrainerName, " +
                            "Metagame, Country, Region) " +
                            "VALUES (@pid, @header, @data, unhex(md5(CONCAT(@header, @data))), " +
                            "UTC_TIMESTAMP(), 1, @streak, @trainer, @metagame, @country, @region); " +
                            "SELECT LAST_INSERT_ID()",
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@header", record.Header.Data),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@streak", record.Header.Streak),
                            new MySqlParameter("@trainer", record.Header.TrainerName),
                            new MySqlParameter("@metagame", (byte)record.Header.Metagame),
                            new MySqlParameter("@country", (byte)record.Header.Country),
                            new MySqlParameter("@region", (byte)record.Header.Region)
                            ));
                        ulong serial = BattleVideoHeader4.KeyToSerial(key);

                        tran.ExecuteNonQuery("UPDATE TerminalBattleVideos5 SET " +
                            "SerialNumber = @serial WHERE id = @key",
                            new MySqlParameter("@serial", serial),
                            new MySqlParameter("@key", key));

                        // todo: make a proc to insert both video and party.
                        InsertBattleVideoParty5(record.Header, key, tran);

                        tran.Commit();
                        return serial;
                    }
                    else
                    {
                        ulong key = (ulong)BattleVideoHeader4.SerialToKey(record.SerialNumber);

                        int rows = tran.ExecuteNonQuery("INSERT INTO TerminalBattleVideos5 " +
                            "(id, pid, SerialNumber, Header, Data, md5, TimeAdded, " +
                            "ParseVersion, Streak, TrainerName, " +
                            "Metagame, Country, Region) " +
                            "VALUES (@key, @pid, @serial, @header, @data, " +
                            "unhex(md5(CONCAT(@header, @data))), " +
                            "UTC_TIMESTAMP(), 1, @streak, @trainer, @metagame, @country, @region)",
                            new MySqlParameter("@key", key),
                            new MySqlParameter("@pid", record.PID),
                            new MySqlParameter("@serial", record.SerialNumber),
                            new MySqlParameter("@header", record.Header.Data),
                            new MySqlParameter("@data", record.Data),
                            new MySqlParameter("@streak", record.Header.Streak),
                            new MySqlParameter("@trainer", record.Header.TrainerName),
                            new MySqlParameter("@metagame", (byte)record.Header.Metagame),
                            new MySqlParameter("@country", (byte)record.Header.Country),
                            new MySqlParameter("@region", (byte)record.Header.Region)
                            );

                        if (rows == 0) return 0;

                        InsertBattleVideoParty5(record.Header, key, tran);

                        tran.Commit();
                        return record.SerialNumber;
                    }
                }
            }
        }

        private void InsertBattleVideoParty5(BattleVideoHeader5 header, ulong key, MySqlTransaction tran)
        {
            MySqlCommand cmd = new MySqlCommand("INSERT INTO " +
            "TerminalBattleVideoPokemon5 (video_id, Slot, Species) VALUES " +
            "(@key, @slot, @species)", tran.Connection, tran);
            cmd.Parameters.Add("@key", MySqlDbType.UInt64).Value = key;
            cmd.Parameters.Add("@slot", MySqlDbType.UByte);
            cmd.Parameters.Add("@species", MySqlDbType.UInt16);

            ushort[] party = header.Party;
            for (byte x = 0; x < 12; x++)
            {
                ushort species = party[x];
                if (species == 0) continue;
                cmd.Parameters["@slot"].Value = x;
                cmd.Parameters["@species"].Value = species;
                cmd.ExecuteNonQuery();
            }
        }

        public override BattleVideoHeader5[] BattleVideoSearch5(ushort species, BattleVideoRankings5 ranking, BattleVideoMetagames5 metagame, byte country, byte region, int count)
        {
            using (MySqlConnection db = CreateConnection())
            {
                List<MySqlParameter> _params = new List<MySqlParameter>();
                String where = "";
                String sort = "";
                bool hasSearch = false;

                if (ranking == BattleVideoRankings5.None)
                {
                    if (species != 0xffff)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") +
                            "EXISTS(SELECT * FROM TerminalBattleVideoPokemon5 " +
                            "WHERE video_id = TerminalBattleVideos5.id AND Species = @species)";
                        _params.Add(new MySqlParameter("@species", species));
                        hasSearch = true;
                    }

                    if (metagame == BattleVideoMetagames5.RandomMatchupSingle)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame IN (40, 104)";
                        hasSearch = true;
                    }
                    else if (metagame == BattleVideoMetagames5.RandomMatchupDouble)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame IN (41, 105)";
                        hasSearch = true;
                    }
                    else if (metagame == BattleVideoMetagames5.RandomMatchupTriple)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame IN (42, 106)";
                        hasSearch = true;
                    }
                    else if (metagame == BattleVideoMetagames5.RandomMatchupRotation)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame IN (43, 107)";
                        hasSearch = true;
                    }
                    else if (metagame == BattleVideoMetagames5.SearchBattleCompetition)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame BETWEEN 56 AND 59";
                        hasSearch = true;
                    }
                    else if (metagame != BattleVideoMetagames5.SearchNone)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Metagame = @metagame";
                        _params.Add(new MySqlParameter("@metagame", (byte)metagame));
                        hasSearch = true;
                    }

                    if (country != 0xff)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Country = @country";
                        _params.Add(new MySqlParameter("@country", country));
                        hasSearch = true;
                    }

                    if (region != 0xff)
                    {
                        where += (hasSearch ? " AND " : " WHERE ") + "Region = @region";
                        _params.Add(new MySqlParameter("@region", region));
                    }

                    sort = " ORDER BY TimeAdded DESC, id DESC";
                }
                else if (ranking == BattleVideoRankings5.LinkBattles)
                {
                    // todo: sort by .. something.
                    where = " WHERE NOT (Metagame BETWEEN 0 AND 4)";
                    sort = " ORDER BY Streak DESC, TimeAdded DESC, id DESC";
                }
                else if (ranking == BattleVideoRankings5.SubwayBattles)
                {
                    where = " WHERE Metagame BETWEEN 0 AND 4";
                    sort = " ORDER BY Streak DESC, TimeAdded DESC, id DESC";
                }
                else
                {
                    sort = " ORDER BY TimeAdded DESC, id DESC";
                }

                _params.Add(new MySqlParameter("@count", count));

                db.Open();

                List<BattleVideoHeader5> results = new List<BattleVideoHeader5>(count);
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT pid, " +
                    "SerialNumber, Header FROM TerminalBattleVideos5" + where +
                    sort + " LIMIT @count",
                    _params.ToArray());
                while (reader.Read())
                {
                    results.Add(BattleVideoHeader5FromReader(reader));
                }

                reader.Close();
                db.Close();
                return results.ToArray();
            }
        }

        private BattleVideoHeader5 BattleVideoHeader5FromReader(MySqlDataReader reader)
        {
            byte[] data = new byte[196];
            reader.GetBytes(2, 0, data, 0, 196);

            return new BattleVideoHeader5(reader.GetInt32(0), reader.GetUInt64(1), data);
        }

        public override BattleVideoRecord5 BattleVideoGet5(ulong serial, bool incrementViews = false)
        {
            String update = incrementViews ? "UPDATE TerminalBattleVideos5 " +
                "SET Views = Views + 1 WHERE SerialNumber = @serial; " 
                : "";

            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader(update + "SELECT pid, " +
                    "SerialNumber, Header, Data FROM TerminalBattleVideos5 " +
                    "WHERE SerialNumber = @serial",
                    new MySqlParameter("@serial", serial));

                if (reader.Read())
                    return BattleVideo5FromReader(reader);
                else return null;
            }
        }

        private BattleVideoRecord5 BattleVideo5FromReader(MySqlDataReader reader)
        {
            byte[] data = new byte[6112];
            reader.GetBytes(3, 0, data, 0, 6112);
            BattleVideoHeader5 header = BattleVideoHeader5FromReader(reader);

            return new BattleVideoRecord5(header.PID, header.SerialNumber, header, data);
        }

        public override bool BattleVideoFlagSaved5(ulong serial)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                int results = db.ExecuteNonQuery("UPDATE TerminalBattleVideos5 " +
                    "SET Saves = Saves + 1 WHERE SerialNumber = @serial",
                    new MySqlParameter("@serial", serial));
                db.Close();

                return results > 0;
            }
        }

        public override ulong BattleVideoCount5()
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();
                return Convert.ToUInt64(db.ExecuteScalar("SELECT Count(*) FROM TerminalBattleVideos5"));
            }
        }
        #endregion

        #region Pokedex creation
        private const String INSERT_COLUMNS = "Name_JA, Name_EN, Name_FR, Name_IT, Name_DE, Name_ES, Name_KO";
        private const String INSERT_VALUES = "@name_ja, @name_en, @name_fr, @name_it, @name_de, @name_es, @name_ko";

        private void CreateLocalizedStringQueryPieces(LocalizedString s, List<MySqlParameter> insertParams)
        {
            String[] langs = new String[] { "JA", "EN", "FR", "IT", "DE", "ES", "KO" };
            foreach (String lang in langs)
            {
                MySqlParameter param = new MySqlParameter("@name_" + lang.ToLowerInvariant(), s.ContainsKey(lang) ? s[lang] : (object)DBNull.Value);
                insertParams.Add(param);
            }
        }

        public override void PokedexInsertSpecies(Species s)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<MySqlParameter> insertParams = new List<MySqlParameter>();
                insertParams.Add(new MySqlParameter("@national_dex", s.NationalDex));
                insertParams.Add(new MySqlParameter("@family_id", s.FamilyID));
                insertParams.Add(new MySqlParameter("@growth_rate", (int)s.GrowthRate));
                insertParams.Add(new MySqlParameter("@gender_ratio", s.GenderRatio));
                insertParams.Add(new MySqlParameter("@egg_group_1", (byte)s.EggGroup1));
                insertParams.Add(new MySqlParameter("@egg_group_2", (byte)s.EggGroup2));
                insertParams.Add(new MySqlParameter("@egg_steps", s.EggSteps));
                insertParams.Add(new MySqlParameter("@gender_variations", s.GenderVariations));
                CreateLocalizedStringQueryPieces(s.Name, insertParams);

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_pokemon (NationalDex, family_id, " +
                    INSERT_COLUMNS + ", GrowthRate, GenderRatio, EggGroup1, EggGroup2, EggSteps, " +
                    "GenderVariations) VALUES (@national_dex, @family_id, " +
                    INSERT_VALUES + ", @growth_rate, @gender_ratio, @egg_group_1, @egg_group_2, " +
                    "@egg_steps, @gender_variations)", insertParams.ToArray());

                db.Close();
            }
        }

        public override void PokedexInsertForm(Form f)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<MySqlParameter> insertParams = new List<MySqlParameter>();
                insertParams.Add(new MySqlParameter("@id", f.ID));
                insertParams.Add(new MySqlParameter("@national_dex", f.SpeciesID));
                insertParams.Add(new MySqlParameter("@form_value", f.Value));
                insertParams.Add(new MySqlParameter("@form_suffix", f.Suffix));
                insertParams.Add(new MySqlParameter("@height", f.Height));
                insertParams.Add(new MySqlParameter("@weight", f.Weight));
                insertParams.Add(new MySqlParameter("@experience", f.Experience));
                CreateLocalizedStringQueryPieces(f.Name, insertParams);

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_pokemon_forms (id, NationalDex, " +
                    "FormValue, " +
                    INSERT_COLUMNS + ", FormSuffix, Height, Weight, Experience) VALUES (" +
                    "@id, @national_dex, @form_value, " +
                    INSERT_VALUES + ", @form_suffix, @height, @weight, @experience)", insertParams.ToArray());

                db.Close();
            }
        }

        public override void PokedexInsertFormStats(FormStats f)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_pokemon_form_stats " +
                    "(form_id, MinGeneration, Type1, Type2, " +
                    "BaseHP, BaseAttack, BaseDefense, BaseSpeed, BaseSpAttack, BaseSpDefense, " +
                    "RewardHP, RewardAttack, RewardDefense, RewardSpeed, RewardSpAttack, RewardSpDefense) " +
                    "VALUES (@form_id, @min_generation, @type1, @type2, " +
                    "@base_hp, @base_attack, @base_defense, @base_speed, @base_sp_attack, @base_sp_defense, " +
                    "@reward_hp, @reward_attack, @reward_defense, @reward_speed, @reward_sp_attack, @reward_sp_defense)",
                    new MySqlParameter("@form_id", f.FormID),
                    new MySqlParameter("@min_generation", (int)f.MinGeneration),
                    new MySqlParameter("@type1", f.Type1ID),
                    new MySqlParameter("@type2", f.Type2ID),
                    new MySqlParameter("@base_hp", f.BaseStats.Hp),
                    new MySqlParameter("@base_attack", f.BaseStats.Attack),
                    new MySqlParameter("@base_defense", f.BaseStats.Defense),
                    new MySqlParameter("@base_speed", f.BaseStats.Speed),
                    new MySqlParameter("@base_sp_attack", f.BaseStats.SpecialAttack),
                    new MySqlParameter("@base_sp_defense", f.BaseStats.SpecialDefense),
                    new MySqlParameter("@reward_hp", (byte)f.RewardEvs.Hp),
                    new MySqlParameter("@reward_attack", (byte)f.RewardEvs.Attack),
                    new MySqlParameter("@reward_defense", (byte)f.RewardEvs.Defense),
                    new MySqlParameter("@reward_speed", (byte)f.RewardEvs.Speed),
                    new MySqlParameter("@reward_sp_attack", (byte)f.RewardEvs.SpecialAttack),
                    new MySqlParameter("@reward_sp_defense", (byte)f.RewardEvs.SpecialDefense)
                );

                db.Close();
            }
        }

        public override void PokedexInsertFamily(Family f)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_pokemon_families " +
                    "(id, BasicMale, BasicFemale, BabyMale, BabyFemale, " +
                    "Incense, GenderRatio) VALUES (@id, @basic_male, " +
                    "@basic_female, @baby_male, @baby_female, @incense, " +
                    "@gender_ratio)",
                    // todo: collapse 0 to null sometimes
                    new MySqlParameter("@id", f.ID),
                    new MySqlParameter("@basic_male", f.BasicMaleID),
                    new MySqlParameter("@basic_female", f.BasicFemaleID),
                    new MySqlParameter("@baby_male", f.BabyMaleID),
                    new MySqlParameter("@baby_female", f.BabyFemaleID),
                    new MySqlParameter("@incense", f.IncenseID),
                    new MySqlParameter("@gender_ratio", f.GenderRatio)
                    );

                db.Close();
            }
        }

        public override void PokedexInsertEvolution(Evolution f)
        {
            throw new NotImplementedException();
        }

        public override void PokedexInsertType(Pokedex.Type t)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<MySqlParameter> insertParams = new List<MySqlParameter>();
                insertParams.Add(new MySqlParameter("@id", t.ID));
                insertParams.Add(new MySqlParameter("@damage_class", (byte)t.DamageClass));
                CreateLocalizedStringQueryPieces(t.Name, insertParams);

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_types (id, " +
                    INSERT_COLUMNS + ", DamageClass) VALUES (@id, " +
                    INSERT_VALUES + ", @damage_class)", insertParams.ToArray());

                db.Close();
            }
        }

        public override void PokedexInsertItem(Item i)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<MySqlParameter> insertParams = new List<MySqlParameter>();
                insertParams.Add(new MySqlParameter("@id", i.ID));
                insertParams.Add(new MySqlParameter("@value3", i.Value3));
                insertParams.Add(new MySqlParameter("@value4", i.Value4));
                insertParams.Add(new MySqlParameter("@value5", i.Value5));
                insertParams.Add(new MySqlParameter("@value6", i.Value6));
                insertParams.Add(new MySqlParameter("@price", i.Price));
                CreateLocalizedStringQueryPieces(i.Name, insertParams);

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_items (id, Value3, " +
                    "Value4, Value5, Value6, " + INSERT_COLUMNS + ", Price) VALUES (" +
                    "@id, @value3, @value4, @value5, @value6, " + INSERT_VALUES +
                    ", @price)", insertParams.ToArray());

                db.Close();
            }
        }

        public override void PokedexInsertMove(Move m)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<MySqlParameter> insertParams = new List<MySqlParameter>();
                insertParams.Add(new MySqlParameter("@value", m.ID));
                insertParams.Add(new MySqlParameter("@type_id", m.TypeID));
                insertParams.Add(new MySqlParameter("@damage_class", (int)m.DamageClass));
                insertParams.Add(new MySqlParameter("@damage", m.Damage));
                insertParams.Add(new MySqlParameter("@pp", m.PP));
                insertParams.Add(new MySqlParameter("@accuracy", m.Accuracy));
                insertParams.Add(new MySqlParameter("@priority", m.Priority));
                insertParams.Add(new MySqlParameter("@target", (int)m.Target));
                CreateLocalizedStringQueryPieces(m.Name, insertParams);

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_moves (Value, type_id, " +
                    "DamageClass, " + INSERT_COLUMNS + ", Damage, PP, Accuracy, " +
                    "Priority, Target) VALUES (@value, @type_id, @damage_class, " + 
                    INSERT_VALUES + ", @damage, @pp, " +
                    "@accuracy, @priority, @target)", insertParams.ToArray());

                db.Close();
            }
        }

        public override void PokedexInsertAbility(Ability a)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                List<MySqlParameter> insertParams = new List<MySqlParameter>();
                insertParams.Add(new MySqlParameter("@value", a.Value));
                CreateLocalizedStringQueryPieces(a.Name, insertParams);

                db.ExecuteNonQuery("INSERT INTO pkmncf_pokedex_abilities (Value, " +
                    INSERT_COLUMNS + ") VALUES (@value, " + INSERT_VALUES + ")",
                    insertParams.ToArray());

                db.Close();
            }
        }

        #endregion

        #region Pokedex retrieval
        private List<T> ReaderToList<T>(MySqlDataReader reader, Pokedex.Pokedex pokedex, Func<T> ctor) 
            where T : PokedexRecordBase
        {
            List<T> result = new List<T>();
            while (reader.Read())
            {
                result.Add(ctor());
            }
            return result;
        }

        public override List<Species> PokedexGetAllSpecies(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "NationalDex, family_id, " + INSERT_COLUMNS + ", GrowthRate, " +
                    "GenderRatio, EggGroup1, EggGroup2, EggSteps, GenderVariations " +
                    "FROM pkmncf_pokedex_pokemon"))
                {
                    return ReaderToList(reader, pokedex, () => new Species(pokedex, reader));
                }
            }
        }

        public override List<Form> PokedexGetAllForms(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "id, NationalDex, FormValue, " + INSERT_COLUMNS + ", FormSuffix, " +
                    "Height, Weight, Experience " +
                    "FROM pkmncf_pokedex_pokemon_forms"))
                {
                    return ReaderToList(reader, pokedex, () => new Form(pokedex, reader));
                }
            }
        }

        public override List<FormStats> PokedexGetAllFormStats(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "form_id, MinGeneration, Type1, Type2, BaseHP, BaseAttack, " +
                    "BaseDefense, BaseSpeed, BaseSpAttack, BaseSpDefense, RewardHP, " +
                    "RewardAttack, RewardDefense, RewardSpeed, RewardSpAttack, RewardSpDefense " +
                    "FROM pkmncf_pokedex_pokemon_form_Stats"))
                {
                    return ReaderToList(reader, pokedex, () => new FormStats(pokedex, reader));
                }
            }
        }

        public override List<Family> PokedexGetAllFamilies(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "id, BasicMale, BasicFemale, BabyMale, BabyFemale, Incense, GenderRatio " +
                    "FROM pkmncf_pokedex_pokemon_families"))
                {
                    return ReaderToList(reader, pokedex, () => new Family(pokedex, reader));
                }
            }
        }

        public override List<Evolution> PokedexGetAllEvolutions(Pokedex.Pokedex pokedex)
        {
            throw new NotImplementedException();
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "id " +
                    "FROM pkmncf_pokedex_pokemon_evolutions"))
                {
                    //return ReaderToList(reader, pokedex, () => new Evolution(pokedex, reader));
                }
            }
        }

        public override List<Pokedex.Type> PokedexGetAllTypes(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "id, " + INSERT_COLUMNS + ", DamageClass " +
                    "FROM pkmncf_pokedex_types"))
                {
                    return ReaderToList(reader, pokedex, () => new Pokedex.Type(pokedex, reader));
                }
            }
        }

        public override List<Item> PokedexGetAllItems(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "id, Value3, Value4, Value5, Value6, " + INSERT_COLUMNS +
                    ", Price " +
                    "FROM pkmncf_pokedex_items"))
                {
                    return ReaderToList(reader, pokedex, () => new Item(pokedex, reader));
                }
            }
        }

        public override List<Move> PokedexGetAllMoves(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "Value, type_id, DamageClass, " + INSERT_COLUMNS + ", Damage, " +
                    "PP, Accuracy, Priority, Target " +
                    "FROM pkmncf_pokedex_moves"))
                {
                    return ReaderToList(reader, pokedex, () => new Move(pokedex, reader));
                }
            }
        }

        public override List<Ability> PokedexGetAllAbilities(Pokedex.Pokedex pokedex)
        {
            using (MySqlConnection db = CreateConnection())
            {
                db.Open();

                using (MySqlDataReader reader = (MySqlDataReader)db.ExecuteReader("SELECT " +
                    "Value, " + INSERT_COLUMNS + 
                    " FROM pkmncf_pokedex_abilities"))
                {
                    return ReaderToList(reader, pokedex, () => new Ability(pokedex, reader));
                }
            }
        }
        #endregion

    }
}
