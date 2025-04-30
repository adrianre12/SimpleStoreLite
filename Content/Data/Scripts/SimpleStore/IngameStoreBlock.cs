using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using VRage.Game.Components;
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

        List<string> BlacklistItems = new List<string> { "RestrictedConstruction", "CubePlacerItem", "GoodAIRewardPunishmentTool" };

        IMyStoreBlock myStoreBlock;
        MyIni config = new MyIni();

        private static Random rnd = new Random();

        bool lastBlockEnabledState = false;
        bool UpdateShop = true;
        int UpdateCounter = 0;
        int refreshCounterLimit = DefaultRefreshPeriod;
        bool debugLog = false;

        List<IMyPlayer> Players = new List<IMyPlayer>();
        List<Sandbox.ModAPI.Ingame.MyStoreQueryItem> StoreItems = new List<Sandbox.ModAPI.Ingame.MyStoreQueryItem>();

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;

            myStoreBlock = Entity as IMyStoreBlock;

            if (!MyAPIGateway.Session.IsServer)
                return;

            MyLog.Default.WriteLine("SimpleStoreLite.StoreBlock: Loaded...");
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

                UpdateShop = true;
            }

            if (!myStoreBlock.IsWorking)
                return;

            UpdateCounter++;

            if (!UpdateShop && UpdateCounter <= refreshCounterLimit)
                return;

            MyLog.Default.WriteLine("SimpleStoreLite.StoreBlock: Starting to update store");

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
                    MyLog.Default.WriteLine("SimpleStore.StoreBlock: Config error in Custom Data.");

                    continue;
                }

                var currentInvItemAmount = MyVisualScriptLogicProvider.GetEntityInventoryItemAmount(myStoreBlock.Name, definition.Id);
                MyVisualScriptLogicProvider.RemoveFromEntityInventory(myStoreBlock.Name, definition.Id, currentInvItemAmount);
                itemConfig.Buy.SetResellCount(currentInvItemAmount);

                var prefab = MyDefinitionManager.Static.GetPrefabDefinition(definition.Id.SubtypeName); // not sure if this is still needed.

                var result = Sandbox.ModAPI.Ingame.MyStoreInsertResults.Success;

                long id;
                MyStoreItemData itemData;

                //sell from the players point of view
                int sellCount = itemConfig.Sell.Count;

                if (prefab == null && sellCount > 0)
                {
                    itemData = new MyStoreItemData(definition.Id, sellCount, itemConfig.Sell.Price, null, null);

                    MyLog.Default.WriteLineIf(debugLog, $"SimpleStoreLite.StoreBlock: InsertOrder {definition.Id.SubtypeName}  Count={sellCount} Price={itemConfig.Sell.Price}");
                    result = myStoreBlock.InsertOrder(itemData, out id);
                    if (result != Sandbox.ModAPI.Ingame.MyStoreInsertResults.Success)
                        MyLog.Default.WriteLine($"SimpleStoreLite.StoreBlock: Sell result {definition.Id.SubtypeName}: {result}");
                }

                //buy
                int buyCount = itemConfig.Buy.Count;
                if (buyCount > 0)
                {
                    itemData = new MyStoreItemData(definition.Id, buyCount, itemConfig.Buy.Price, null, null);

                    MyLog.Default.WriteLineIf(debugLog, $"SimpleStoreLite.StoreBlock: InsertOffer {definition.Id.SubtypeName} Count={buyCount} Price={itemConfig.Buy.Price}");
                    result = myStoreBlock.InsertOffer(itemData, out id);

                    if (result == Sandbox.ModAPI.Ingame.MyStoreInsertResults.Success)
                        MyVisualScriptLogicProvider.AddToInventory(myStoreBlock.Name, definition.Id, buyCount);
                    else
                        MyLog.Default.WriteLine($"SimpleStoreLite.StoreBlock: Buy result {definition.Id.SubtypeName}: {result}");

                }
            }

            UpdateCounter = 0;
            if (UpdateShop)
            {
                UpdateCounter = rnd.Next((int)(refreshCounterLimit - MinRefreshPeriod * 0.2)); // stop them all refreshing at the same time.
                MyLog.Default.WriteLineIf(debugLog, $"SimpleStoreLite.StoreBlock: UpdateCounter={UpdateCounter}");
            }
            UpdateShop = false;
        }


        private string FixKey(string key)
        {
            return key.Replace('[', '{').Replace(']', '}'); // Replace [ ]  with { } for mods like Better Stone
        }

        private void CreateConfig()
        {
            MyLog.Default.WriteLine("SimpleStoreLite.StoreBlock: Start CreateConfig");

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

            string section;
            Match match;

            ItemConfig defaultItemConfig = new ItemConfig();
            string subtypeName = "";

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
                    defaultItemConfig.SetDefaultPrices(definition.Id);
                    config.Set(section, subtypeName, defaultItemConfig.ToString());
                }
            }

            config.Invalidate();
            myStoreBlock.CustomData = config.ToStringSorted();
        }

        private bool TryLoadConfig()
        {
            MyLog.Default.WriteLine("SimpleStoreLite.StoreBlock: Loading Config");

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
                            MyLog.Default.WriteLine($"SimpleStoreLite.StoreBlock: Failed to get {section}:{key.Name}");
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
                    MyLog.Default.WriteLine("SimpleStoreLite.StoreBlock: Config Value error");
                    myStoreBlock.Enabled = false;
                }

            }
            else
            {
                configOK = false;
                MyLog.Default.WriteLine("SimpleStoreLite.StoreBlock: Config Syntax error");
                myStoreBlock.Enabled = false;
            }
            return configOK;
        }
    }
}