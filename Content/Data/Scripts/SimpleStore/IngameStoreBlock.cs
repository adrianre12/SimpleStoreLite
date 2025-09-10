using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace SimpleStoreLite.StoreBlock
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_StoreBlock), false, "SimpleStoreLiteBlock")]
    public class IngameStoreBlockGameLogic : MyGameLogicComponent
    {
        const string ConfigSettings = "Settings";
        const string RefreshPeriod = "RefreshPeriodMins";
        const string DebugLog = "Debug";

        const int DefaultRefreshPeriod = 20; //mins
        const int MinRefreshPeriod = 375;  // 100's of ticks mins * 37.5

        private List<string> BlacklistItems = new List<string> { "RestrictedConstruction", "CubePlacerItem", "GoodAIRewardPunishmentTool", "SpaceCredit", "SkeletonKey", "UraniumB" };
        private List<string> WhitelistItems = new List<string> { "NATO_5p56x45mm", "Organic", "Scrap" };

        private IMyStoreBlock myStoreBlock;
        private MyIni config = new MyIni();

        private static Random rnd = new Random();

        private bool lastBlockEnabledState = false;
        private bool updateShop = true;
        private int updateCounter = 0;
        private int refreshCounterLimit = DefaultRefreshPeriod;
        private bool debugLog = false;

        private List<Sandbox.ModAPI.Ingame.MyStoreQueryItem> StoreItems = new List<Sandbox.ModAPI.Ingame.MyStoreQueryItem>();

        private Dictionary<MyDefinitionId, int> inventoryItems = new Dictionary<MyDefinitionId, int>();
        private HashSet<MyCubeBlock> inventoryBlocks;
        private IMyInventory myStoreInventory;

        public void LogMsg(string msg)
        {
            MyLog.Default.WriteLine($"SimpleStoreLite.StoreBlock: {msg}");
        }

        public void DebugMsg(string msg)
        {
            if (debugLog)
                LogMsg($"[DEBUG] {msg}");
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;

            myStoreBlock = Entity as IMyStoreBlock;
            myStoreInventory = myStoreBlock.GetInventory(0);

            if (!MyAPIGateway.Session.IsServer)
                return;

            LogMsg("Loaded...");
        }

        public override void UpdateAfterSimulation100()
        {
            if (myStoreBlock.CustomData == "")
                CreateConfig();

            if (!MyAPIGateway.Session.IsServer)
                return;

            if (myStoreBlock.Enabled != lastBlockEnabledState)
            {
                lastBlockEnabledState = myStoreBlock.Enabled;

                if (myStoreBlock.Enabled == false)
                    return;

                if (!TryLoadConfig())
                    return;

                updateShop = true;
            }

            if (!myStoreBlock.IsWorking)
                return;

            updateCounter++;

            if (!updateShop && updateCounter <= refreshCounterLimit)
                return;

            GetGridInventory();

            UpdateStore();

            updateCounter = 0;
            if (updateShop)
            {
                updateCounter = rnd.Next((int)(refreshCounterLimit - MinRefreshPeriod * 0.2)); // stop them all refreshing at the same time.
                DebugMsg($"UpdateCounter={updateCounter}");
            }
            updateShop = false;
        }

        private void GetGridInventory()
        {
            LogMsg("Starting GetGridInventory.");
            inventoryBlocks = (myStoreBlock.CubeGrid as MyCubeGrid).Inventories;

            inventoryItems.Clear();


            foreach (var block in inventoryBlocks)
            {
                var inv = block.GetInventory(block.InventoryCount - 1) as MyInventory;
                if (!myStoreInventory.IsConnectedTo(inv))
                {
                    continue;
                }

                DebugMsg($"Block {block.DisplayNameText} invCount={block.InventoryCount} size={inv.ItemCount}");

                foreach (MyPhysicalInventoryItem item in inv.GetItems())
                {
                    DebugMsg($"   {item.Content.SubtypeId}  {item.Amount}");
                    var defId = item.Content.GetId();

                    if (inventoryItems.ContainsKey(defId))
                    {
                        /*                        if (largestStackOnly)
                                                {
                                                    inventoryItems[defId] = Math.Max(inventoryItems[defId], item.Amount.ToIntSafe());
                                                }
                                                else
                                                { */
                        inventoryItems[defId] += item.Amount.ToIntSafe();
                        //                        }
                    }
                    else
                    {
                        inventoryItems[defId] = item.Amount.ToIntSafe();
                    }
                }
            }

            if (debugLog)
            {
                DebugMsg($"InventoryItems {inventoryItems.Count}");
                foreach (var item in inventoryItems)
                {
                    DebugMsg($"Item {item.Key.SubtypeName} ammount {item.Value}");
                }
            }
        }

        private void UpdateStore()
        {
            LogMsg("Starting to update store.");

            myStoreBlock.GetPlayerStoreItems(StoreItems);

            foreach (var item in StoreItems)
            {
                myStoreBlock.CancelStoreItem(item.Id);
            }

            Match match;
            string rawIniValue;
            ItemConfig itemConfig = new ItemConfig();
            string subtypeName = "";

            foreach (var definition in MyDefinitionManager.Static.GetAllDefinitions())
            {
                subtypeName = FixKey(definition.Id.SubtypeName);
                match = Regex.Match(definition.Id.TypeId.ToString() + subtypeName, @"[\[\]\r\n|=]");
                if (match.Success)
                    continue;

                var section = definition.Id.TypeId.ToString().Remove(0, 16); //remove "MyObjectBuilder_"

                if (!config.ContainsSection(section) || !config.ContainsKey(section, subtypeName))
                    continue;

                if (!config.Get(section, subtypeName).TryGetString(out rawIniValue))
                    continue;

                if (!itemConfig.TryParse(rawIniValue))
                {
                    myStoreBlock.Enabled = false;
                    LogMsg("Config error in Custom Data.");

                    continue;
                }

                //var currentInvItemAmount = MyVisualScriptLogicProvider.GetEntityInventoryItemAmount(myStoreBlock.Name, definition.Id);
                var currentInvItemAmount = inventoryItems.GetValueOrNew(definition.Id);
                itemConfig.Buy.SetResellCount(currentInvItemAmount);

                var prefab = MyDefinitionManager.Static.GetPrefabDefinition(definition.Id.SubtypeName);

                var result = Sandbox.ModAPI.Ingame.MyStoreInsertResults.Success;

                long id;
                MyStoreItemData itemData;

                //sell from the players point of view
                int sellCount = itemConfig.Sell.Count;

                if (prefab == null && sellCount > 0)
                {
                    itemData = new MyStoreItemData(definition.Id, sellCount, itemConfig.Sell.Price, null, null);

                    DebugMsg("InsertOrder {definition.Id.SubtypeName}  Count={sellCount} Price={itemConfig.Sell.Price}");
                    result = myStoreBlock.InsertOrder(itemData, out id);
                    if (result != Sandbox.ModAPI.Ingame.MyStoreInsertResults.Success)
                        LogMsg("Sell result {definition.Id.SubtypeName}: {result}");
                }

                //buy
                int buyCount = itemConfig.Buy.Count;
                if (buyCount > 0)
                {
                    itemData = new MyStoreItemData(definition.Id, buyCount, itemConfig.Buy.Price, null, null);

                    DebugMsg($"InsertOffer {definition.Id.SubtypeName} Count={buyCount} Price={itemConfig.Buy.Price}");
                    result = myStoreBlock.InsertOffer(itemData, out id);

                    if (result != Sandbox.ModAPI.Ingame.MyStoreInsertResults.Success)
                        LogMsg($"Buy result {definition.Id.SubtypeName}: {result}");
                }
            }

        }
        private string FixKey(string key)
        {
            return key.Replace('[', '{').Replace(']', '}'); // Replace [ ]  with { } for mods like Better Stone
        }

        private void CreateConfig()
        {
            LogMsg("Start CreateConfig");

            config.Clear();
            config.AddSection(ConfigSettings);
            var sb = new StringBuilder();
            sb.AppendLine("Do not activate too many items, the store has a total of 30 slots.");
            sb.AppendLine("Auto-generated prices are the Keen minimum, setting lower value will log an error.");
            sb.AppendLine("To force a store refresh, turn store block off wait 3s and turn it on. Auto refresh minimum and default is 20 minutes");
            sb.AppendLine("Config errors will cause the store block to turn off");
            sb.AppendLine("Format is BuyAmount:BuyPrice,SellAmount:SellPrice");
            config.SetSectionComment(ConfigSettings, sb.ToString());

            config.Set(ConfigSettings, RefreshPeriod, DefaultRefreshPeriod);

            config.AddSection("Ore");
            config.AddSection("Ingot");
            config.AddSection("Component");
            config.AddSection("PhysicalGunObject");
            config.AddSection("AmmoMagazine");
            config.AddSection("OxygenContainerObject");
            config.AddSection("GasContainerObject");
            config.AddSection("ConsumableItem");
            config.AddSection("PhysicalObject");

            string section;
            Match match;

            ItemConfig defaultItemConfig = new ItemConfig();
            string subtypeName = "";
            bool ok;
            foreach (var definition in MyDefinitionManager.Static.GetAllDefinitions())
            {
                if (BlacklistItems.Contains(definition.Id.SubtypeName))
                    continue;
                if (MyDefinitionManager.Static.GetPrefabDefinition(definition.Id.SubtypeName) != null)
                    continue;

                subtypeName = FixKey(definition.Id.SubtypeName);
                match = Regex.Match(definition.Id.TypeId.ToString() + subtypeName, @"[\[\]\r\n|=]");
                if (match.Success)
                    continue;

                section = definition.Id.TypeId.ToString().Remove(0, 16); //remove "MyObjectBuilder_"

                if (config.ContainsSection(section))
                {
                    ok = false;
                    /*                    if (!definition.Public && MyDefinitionManager.Static.GetPrefabDefinition(definition.Id.SubtypeName) != null)
                                        {
                                            ok = true;
                                        }*/
                    if (WhitelistItems.Contains(definition.Id.SubtypeName) || (definition.Public && MyDefinitionManager.Static.GetPhysicalItemDefinition(definition.Id).CanPlayerOrder))
                    {
                        ok = true;
                    }
                    if (ok)
                    {
                        defaultItemConfig.SetDefaultPrices(definition.Id);
                        config.Set(section, subtypeName, defaultItemConfig.ToString());
                        continue;
                    }
                    LogMsg($"skipping {subtypeName} public={definition.Public}");
                }
            }

            config.Invalidate();
            myStoreBlock.CustomData = config.ToStringSorted();
        }

        private bool TryLoadConfig()
        {
            LogMsg("Loading Config");

            bool configOK = true;
            bool storeConfig = false;

            if (config.TryParse(myStoreBlock.CustomData))
            {
                if (!config.ContainsSection(ConfigSettings))
                    configOK = false;

                refreshCounterLimit = Math.Max((int)(config.Get(ConfigSettings, RefreshPeriod).ToInt32(DefaultRefreshPeriod) * 37.5), MinRefreshPeriod);
                debugLog = config.Get(ConfigSettings, DebugLog).ToBoolean(false);

                List<string> sections = new List<string>();
                List<MyIniKey> keys = new List<MyIniKey>();
                ItemConfig itemConfig = new ItemConfig();
                string rawIniValue;

                config.GetSections(sections);
                foreach (var section in sections)
                {
                    config.GetKeys(section, keys);
                    foreach (var key in keys)
                    {
                        if (!config.Get(section, key.Name).TryGetString(out rawIniValue))
                        {
                            configOK = false;
                            LogMsg("Failed to get {section}:{key.Name}");
                            break;
                        }
                        if (section == ConfigSettings)
                        {
                            continue;
                        }

                        if (!itemConfig.TryParse(rawIniValue))
                        {
                            config.Set(section, key.Name, itemConfig.ToString());
                            storeConfig = true;
                            configOK = false;
                            continue;
                        }

                        if (itemConfig.IsRemoveError())
                        {
                            config.Set(section, key.Name, itemConfig.ToString());
                            storeConfig = true;
                        }
                    }
                }

                if (storeConfig)
                {
                    config.Invalidate();
                    myStoreBlock.CustomData = config.ToString();
                }

                if (!configOK)
                {
                    LogMsg("Config Value error");
                    myStoreBlock.Enabled = false;
                }

            }
            else
            {
                configOK = false;
                LogMsg("Config Syntax error");
                myStoreBlock.Enabled = false;
            }
            return configOK;
        }
    }
}