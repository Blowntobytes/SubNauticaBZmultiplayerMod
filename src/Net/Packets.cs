using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BZMultiplayer.Net
{
    /// <summary>Wire-level packet ids. First byte of every datagram.</summary>
    public enum PacketType : byte
    {
        Hello = 1,        // client -> host on join; host -> client with roster
        Welcome = 2,      // host -> client: accepted, here are the other members
        PlayerPose = 10,  // unreliable, high rate
        PlayerLeft = 11,  // host -> clients

        SaveRequest = 20, // client -> host: send me your world
        SaveStatus = 21,  // host -> client: progress / error text
        SaveBegin = 22,   // host -> client: slot name, file count, total bytes
        SaveFile = 23,    // host -> client: file index, relative path, size
        SaveChunk = 24,   // host -> client: file index, offset, bytes
        SaveEnd = 25,     // host -> client: all files sent
        SaveAbort = 26,   // host -> client: transfer failed, reason

        Clock = 30,       // host -> clients: DayNightCycle.timePassed
        ItemPickup = 31,  // any -> host -> others: unique id
        ItemDrop = 32,    // any -> host -> others: techType, id, pos, rot
        Placed = 33,      // any -> host -> others: techType, id, pos, rot, parentId
        Progress = 34,    // any -> host -> others: id, amount
        ContainerAdd = 35,    // containerId, itemId, techType
        ContainerRemove = 36, // containerId, itemId
        BaseState = 38,       // baseId, isNew, pos, rot, blob (Base component serialized) - whole hull shape of one base
        BaseRemoved = 39,     // baseId
        HeldItem = 41,        // owner steam id, techType, flags (bit0 = its light is on)
        Story = 40,           // kind, key, techType (story goal / blueprint / scan / encyclopedia / log)
        ResourceBroken = 37,  // outcrop id (breakable resource smashed; its drops follow as ItemDrop)
        InventoryData = 42,   // worldKey, item count, items (techType + quantity each)
        InventoryRequest = 43, // client -> host: ready for saved inventory
        CutsceneStart = 44,   // any -> host -> others: objectId (PlayerCinematicController on that object)
    }

    [Flags]
    public enum PoseFlags : byte
    {
        None = 0,
        VR = 1,
        Underwater = 2,
        HasHands = 4,
        InVehicle = 8,
        Swimming = 16,   // in water but at/near the surface (not fully submerged)
    }

    /// <summary>Snapshot of a player's transform set. All positions are world space.</summary>
    public struct PlayerPose
    {
        public ulong SteamId;
        public float Time;
        public PoseFlags Flags;
        public Vector3 BodyPos;
        public Quaternion BodyRot;
        public Vector3 HeadPos;
        public Quaternion HeadRot;
        public Vector3 HandLPos;
        public Quaternion HandLRot;
        public Vector3 HandRPos;
        public Quaternion HandRRot;

        public bool IsVR { get { return (Flags & PoseFlags.VR) != 0; } }
        public bool HasHands { get { return (Flags & PoseFlags.HasHands) != 0; } }
    }

    /// <summary>Tiny binary writer over a reusable buffer. Little-endian floats via BitConverter.</summary>
    public sealed class PacketWriter
    {
        private readonly MemoryStream stream = new MemoryStream(512);
        private readonly BinaryWriter w;

        public PacketWriter() { w = new BinaryWriter(stream, Encoding.UTF8); }

        public PacketWriter Begin(PacketType type)
        {
            stream.SetLength(0);
            stream.Position = 0;
            w.Write((byte)type);
            return this;
        }

        public void Write(byte v) { w.Write(v); }
        public void Write(int v) { w.Write(v); }
        public void Write(long v) { w.Write(v); }
        public void Write(double v) { w.Write(v); }
        public void Write(byte[] data, int offset, int count) { w.Write(count); w.Write(data, offset, count); }
        public void Write(ulong v) { w.Write(v); }
        public void Write(float v) { w.Write(v); }
        public void Write(bool v) { w.Write(v); }
        public void Write(string v) { w.Write(v ?? ""); }
        public void Write(Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public void Write(Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }

        public void Write(ref PlayerPose p)
        {
            w.Write(p.SteamId);
            w.Write(p.Time);
            w.Write((byte)p.Flags);
            Write(p.BodyPos); Write(p.BodyRot);
            Write(p.HeadPos); Write(p.HeadRot);
            Write(p.HandLPos); Write(p.HandLRot);
            Write(p.HandRPos); Write(p.HandRRot);
        }

        public byte[] Buffer { get { return stream.GetBuffer(); } }
        public int Length { get { return (int)stream.Length; } }
    }

    public sealed class PacketReader
    {
        private readonly BinaryReader r;
        private readonly MemoryStream stream;

        public PacketReader(byte[] data, int length)
        {
            stream = new MemoryStream(data, 0, length, false);
            r = new BinaryReader(stream, Encoding.UTF8);
        }

        public PacketType ReadType() { return (PacketType)r.ReadByte(); }
        public byte ReadByte() { return r.ReadByte(); }
        public int ReadInt() { return r.ReadInt32(); }
        public long ReadLong() { return r.ReadInt64(); }
        public double ReadDouble() { return r.ReadDouble(); }
        /// <summary>Reads a length-prefixed blob; returns the offset into the underlying buffer (no copy).</summary>
        public int ReadBlob(out int length)
        {
            length = r.ReadInt32();
            int offset = (int)stream.Position;
            stream.Position += length;
            return offset;
        }
        public ulong ReadULong() { return r.ReadUInt64(); }
        public float ReadFloat() { return r.ReadSingle(); }
        public bool ReadBool() { return r.ReadBoolean(); }
        public string ReadString() { return r.ReadString(); }
        public Vector3 ReadVector3() { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        public Quaternion ReadQuaternion() { return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        public bool AtEnd { get { return stream.Position >= stream.Length; } }

        public PlayerPose ReadPose()
        {
            var p = new PlayerPose();
            p.SteamId = r.ReadUInt64();
            p.Time = r.ReadSingle();
            p.Flags = (PoseFlags)r.ReadByte();
            p.BodyPos = ReadVector3(); p.BodyRot = ReadQuaternion();
            p.HeadPos = ReadVector3(); p.HeadRot = ReadQuaternion();
            p.HandLPos = ReadVector3(); p.HandLRot = ReadQuaternion();
            p.HandRPos = ReadVector3(); p.HandRRot = ReadQuaternion();
            return p;
        }
    }
}
