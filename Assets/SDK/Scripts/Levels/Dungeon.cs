﻿using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.Events;
using System.Linq;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.AI;
using System.Text;
using UnityEngine.SceneManagement;

#if DUNGEN
using DunGen;
using DunGen.Adapters;
using DunGen.Graph;
#endif

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#else
using EasyButtons;
#endif

namespace ThunderRoad
{
    public class Dungeon : MonoBehaviour
    {
        public string dungeonFlowAddress = "Bas.DungeonFlow.Greenland";
        public bool cullingEnabled = true;
        public bool staticBatchRooms = true;
        public int seed;

#if DUNGEN

        [NonSerialized, ShowInInspector, ReadOnly]
        public Transform playerTransform;

        [NonSerialized, ShowInInspector, ReadOnly]
        public List<Room> rooms;

        [NonSerialized, ShowInInspector, ReadOnly]
        public Room currentPlayerRoom;

        [NonSerialized]
        public bool initialized;

        public delegate void DungeonGeneratedEvent();
        public event DungeonGeneratedEvent onDungeonGenerated;

        public delegate void PlayerChangeRoomEvent(Room oldRoom, Room newRoom);
        public event PlayerChangeRoomEvent onPlayerChangeRoom;

        public delegate void RoomVisibilityChangeEvent(Room room);
        public event RoomVisibilityChangeEvent onRoomVisibilityChange;

        [NonSerialized]
        public RuntimeDungeon dungeonGenerator;
        [NonSerialized]
        public UnityNavMeshAdapter dungeonNavMeshAdapter;


        private void Awake()
        {
            dungeonNavMeshAdapter = GameObject.FindObjectOfType<UnityNavMeshAdapter>();
            dungeonNavMeshAdapter.enabled = false;

            dungeonGenerator = GameObject.FindObjectOfType<RuntimeDungeon>();
            dungeonGenerator.Generator.OnGenerationStatusChanged += OnGenerationStatusChanged;

            if (!Level.master)
            {
                // If master scene not loaded
                Addressables.LoadAssetAsync<DungeonFlow>(dungeonFlowAddress).Completed += (handle) =>
                {
                    if (handle.Status == AsyncOperationStatus.Succeeded)
                    {
                        dungeonGenerator.Generator.DungeonFlow = handle.Result;
                        GenerateDungeon();
                    }
                    else
                    {
                        Debug.LogError("Could not find dungeon flow asset at address: " + dungeonFlowAddress);
                    }
                };
            }
        }


        [Button]
        public void GenerateDungeon()
        {
            if (dungeonGenerator && Application.isPlaying)
            {
                rooms = new List<Room>();
                initialized = false;
                dungeonGenerator.Generator.ShouldRandomizeSeed = seed > 0 ? false : true;
                dungeonGenerator.Generator.Seed = seed;
                dungeonGenerator.Generate();
            }
        }

        private void OnGenerationStatusChanged(DungeonGenerator generator, GenerationStatus status)
        {
            if (status == GenerationStatus.Complete)
            {
                SceneManager.MoveGameObjectToScene(generator.CurrentDungeon.gameObject, this.gameObject.scene);
                seed = generator.ChosenSeed;
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("Seed: " + generator.ChosenSeed);
                stringBuilder.AppendLine("Generation time: " + generator.GenerationStats.TotalTime + " ms");
                stringBuilder.AppendLine("Main room count: " + generator.GenerationStats.MainPathRoomCount);
                stringBuilder.AppendLine("Branch room count: " + generator.GenerationStats.BranchPathRoomCount);
                stringBuilder.AppendLine("Total room count: " + generator.GenerationStats.TotalRoomCount);
                stringBuilder.AppendLine("Retry count: " + generator.GenerationStats.TotalRetries);
                Debug.Log(stringBuilder.ToString());

                foreach (Tile tile in dungeonGenerator.Generator.CurrentDungeon.AllTiles)
                {
                    rooms.Add(tile.GetComponent<Room>());
                }

                if (rooms.Count == 0)
                {
                    Debug.LogError("Dungeon - No rooms generated?!");
                    return;
                }

                GenerateNavMesh();

                PlayerSpawner playerSpawner = rooms[0].GetPlayerSpawner();
                if (!playerSpawner)
                {
                    Debug.LogError("Dungeon - Starting room don't have any playerSpawner!");
                    return;
                }

                playerTransform = playerSpawner.transform;
                currentPlayerRoom = SearchRoomFromPosition(playerTransform.position);
                initialized = true;
                RefreshCulling();

                if (onDungeonGenerated != null) onDungeonGenerated.Invoke();
            }
        }

        [Button]
        public void GenerateNavMesh()
        {
            if (dungeonNavMeshAdapter && Application.isPlaying)
            {
                dungeonNavMeshAdapter.BakeMode = UnityNavMeshAdapter.RuntimeNavMeshBakeMode.PreBakedOnly;
                dungeonNavMeshAdapter.enabled = true;
                dungeonNavMeshAdapter.Generate(dungeonGenerator.Generator.CurrentDungeon);
            }
        }

        public void InvokeRoomVisibilityChange(Room room)
        {
            if (onRoomVisibilityChange != null) onRoomVisibilityChange.Invoke(room);
        }

        public void GenerateGlobalNavMesh()
        {
            NavMeshSurface surface = this.gameObject.GetComponent<NavMeshSurface>();
            if (!surface) surface = this.gameObject.AddComponent<NavMeshSurface>();
            surface.BuildNavMesh();
        }

        public Room GetRoomPrevious(Room room)
        {
            return Level.current.dungeon.rooms.ElementAtOrDefault(GetRoomIndex(room) - 1);
        }

        public Room GetRoomNext(Room room)
        {
            return Level.current.dungeon.rooms.ElementAtOrDefault(GetRoomIndex(room) + 1);
        }

        public int GetRoomIndex(Room room)
        {
            return Level.current.dungeon.rooms.IndexOf(room);
        }

        private void LateUpdate()
        {
            if (initialized && cullingEnabled && playerTransform)
            {
                if (currentPlayerRoom == null)
                {
                    Room roomFound = SearchRoomFromPosition(playerTransform.position);
                    if (currentPlayerRoom != roomFound)
                    {
                        OnPlayerChangeRoom(currentPlayerRoom, roomFound);
                    }
                }
                else if (!currentPlayerRoom.tile.Bounds.Contains(playerTransform.position))
                {
                    Room roomFound = SearchRoomFromPosition(playerTransform.position, currentPlayerRoom);
                    if (currentPlayerRoom != roomFound)
                    {
                        OnPlayerChangeRoom(currentPlayerRoom, roomFound);
                    }
                }
            }
        }

        public Room SearchRoomFromPosition(Vector3 position, Room fromRoom = null)
        {
            if (fromRoom)
            {
                Room nextRoom = GetRoomNext(fromRoom);
                if (nextRoom && nextRoom.tile.Bounds.Contains(position))
                {
                    return nextRoom;
                }
                Room previousRoom = GetRoomPrevious(fromRoom);
                if (previousRoom && previousRoom.tile.Bounds.Contains(position))
                {
                    return previousRoom;
                }
                foreach (Room room in rooms)
                {
                    if (fromRoom == room || nextRoom == room || previousRoom == room) continue;
                    if (room.tile.Bounds.Contains(position))
                    {
                        return room;
                    }
                }
                return fromRoom;
            }
            else
            {
                foreach (Room room in rooms)
                {
                    if (room.tile.Bounds.Contains(position))
                    {
                        return room;
                    }
                }
                return rooms[0];
            }
        }

        protected void OnPlayerChangeRoom(Room oldRoom, Room newRoom)
        {
            currentPlayerRoom = newRoom;
            RefreshCulling();
            if (oldRoom) oldRoom.OnPlayerExit();
            if (oldRoom) newRoom.OnPlayerEnter();
            if (onPlayerChangeRoom != null) onPlayerChangeRoom.Invoke(oldRoom, newRoom);
        }

        public void RefreshCulling()
        {
            if (cullingEnabled && currentPlayerRoom)
            {
                currentPlayerRoom.SetCull(false);
                Room previousPlayerRoom = GetRoomPrevious(currentPlayerRoom);
                if (previousPlayerRoom) previousPlayerRoom.SetCull(false);
                Room nextPlayerRoom = GetRoomNext(currentPlayerRoom);
                if (nextPlayerRoom) nextPlayerRoom.SetCull(false);
                foreach (Room room in rooms)
                {
                    if (currentPlayerRoom == room || previousPlayerRoom == room || nextPlayerRoom == room) continue;
                    room.SetCull(true);
                }
            }
            else
            {
                foreach (Room room in rooms)
                {
                    room.SetCull(false);
                }
            }
        }

        public void SetCulling(bool enabled)
        {
            cullingEnabled = enabled;
            RefreshCulling();
        }

        [Button]
        public void ToggleCulling()
        {
            cullingEnabled = !cullingEnabled;
            RefreshCulling();
        }
#endif
    }
}