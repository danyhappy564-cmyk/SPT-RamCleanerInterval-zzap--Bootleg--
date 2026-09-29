using System;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Tells whether "now" is a good moment for work that can hitch the game (the final GC phase,
    /// UnloadUnusedAssets, working set trim): the player has not shot, been hit or aimed recently.
    /// Also reports when the inventory is open, which is the best moment of all - nobody notices a
    /// hitch while looking at a grid.
    ///
    /// Everything comes from events the game already raises on the main player (no Harmony patches):
    /// Player.BeingHitAction, Player.OnInventoryOpened, FirearmController.OnShot.
    /// </summary>
    internal sealed class CombatTracker
    {
        private Player _player;
        private Player.FirearmController _firearm;
        private float _lastActivity = float.NegativeInfinity;

        public bool InventoryOpen { get; private set; }

        /// <summary>Seconds since the player last shot, got hit or aimed.</summary>
        public float SecondsSinceCombat => Time.realtimeSinceStartup - _lastActivity;

        /// <summary>Raised on the main thread when the inventory opens.</summary>
        public event Action InventoryOpened;

        /// <summary>Call once per second. Re-binds to a new main player (raid start) and samples aiming.</summary>
        public void Poll()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            Player player = world != null ? world.MainPlayer : null;
            if (!ReferenceEquals(player, _player))
            {
                Bind(player);
            }

            if (_firearm != null && _firearm.IsAiming)
            {
                MarkActivity();
            }
        }

        public bool IsQuiet(float quietSeconds)
        {
            return InventoryOpen || SecondsSinceCombat >= quietSeconds;
        }

        public void Unbind()
        {
            Bind(null);
        }

        private void Bind(Player player)
        {
            if (_player != null)
            {
                _player.BeingHitAction -= OnBeingHit;
                _player.OnInventoryOpened -= OnInventoryOpenedEvent;
                _player.OnHandsControllerChanged -= OnHandsChanged;
            }

            SetFirearm(null);
            _player = player;
            InventoryOpen = false;
            _lastActivity = float.NegativeInfinity;

            if (_player == null)
            {
                return;
            }

            _player.BeingHitAction += OnBeingHit;
            _player.OnInventoryOpened += OnInventoryOpenedEvent;
            _player.OnHandsControllerChanged += OnHandsChanged;
            SetFirearm(_player.HandsController as Player.FirearmController);
        }

        private void OnHandsChanged(Player.AbstractHandsController oldController, Player.AbstractHandsController newController)
        {
            SetFirearm(newController as Player.FirearmController);
        }

        private void SetFirearm(Player.FirearmController firearm)
        {
            if (_firearm != null)
            {
                _firearm.OnShot -= MarkActivity;
            }

            _firearm = firearm;
            if (_firearm != null)
            {
                _firearm.OnShot += MarkActivity;
            }
        }

        private void OnBeingHit(DamageInfo damage, EBodyPart part, float amount)
        {
            MarkActivity();
        }

        private void OnInventoryOpenedEvent(Player player, bool opened)
        {
            InventoryOpen = opened;
            if (opened)
            {
                InventoryOpened?.Invoke();
            }
        }

        private void MarkActivity()
        {
            _lastActivity = Time.realtimeSinceStartup;
        }
    }
}
