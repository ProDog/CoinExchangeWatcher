﻿using log4net;
using Neo.VM;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using Zoro;
using Zoro.IO;
using Zoro.Network.P2P.Payloads;
using Zoro.Wallets;

namespace NFT_API
{
    public class NftServer
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static byte[] ExecRequest(HttpListenerContext requestContext)
        {
            //获取客户端传递的参数
            StreamReader sr = new StreamReader(requestContext.Request.InputStream);
            var reqMethod = requestContext.Request.RawUrl.Replace("/", "");
            var data = sr.ReadToEnd();
            byte[] buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new RspInfo() { }));
            Logger.Info($"Have a request:{reqMethod}; post data:{data}");
            var json = new JObject();
            if (!string.IsNullOrEmpty(data))
                json = JObject.Parse(data);

            RspInfo rspInfo = new RspInfo();

            if (reqMethod == "buy" || reqMethod == "bind" || reqMethod == "upgrade" || reqMethod == "addPoint" || reqMethod == "exchange" || reqMethod == "reduceGrade" || reqMethod == "reducePoint")
                rspInfo = GetSendrawRsp(reqMethod, json);

            else if (reqMethod == "getMoney")
                rspInfo = SendMoney(json);
            else
                rspInfo = GetInvokeRsp(reqMethod, json);


            buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rspInfo));

            return buffer;
        }

        private static RspInfo GetSendrawRsp(string reqMethod, JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            RspInfo rspInfo = new RspInfo();
            decimal gas = 5;
            switch (reqMethod)
            {
                case "addPoint":
                    sb = AddPointBuilder(json);
                    break;
                case "reduceGrade":
                    sb = ReduceGradeBuilder(json);
                    break;
                case "reducePoint":
                    sb = ReducePointBuilder(json);
                    break;
                case "bind":
                    sb = BindBulider(json);
                    break;
                case "buy":
                    sb = BuyBulider(json, ref gas);
                    break;
                case "upgrade":
                    sb = UpgradeBuilder(json);
                    break;
                case "exchange":
                    sb = ExchangeBulider(json);
                    break;
            }

            KeyPair keypair = ZoroHelper.GetKeyPairFromWIF(Config.getStrValue("adminWif"));

            InvocationTransaction tx = ZoroHelper.MakeTransaction(sb.ToArray(), keypair, Fixed8.FromDecimal(gas), Fixed8.One);
            var txid = tx.Hash.ToString();
            var result = ZoroHelper.SendRawTransaction(tx.ToArray().ToHexString(), "");

            var state = (bool)(JObject.Parse(result)["result"]);
            if (state)
            {
                rspInfo = new RspInfo()
                {
                    state = true,
                    msg = txid
                };
            }
            else
            {
                rspInfo = new RspInfo()
                {
                    state = false,
                    msg =  result
                };
            }

            return rspInfo;
        }

        private static RspInfo SendMoney(JObject json)
        {
            RspInfo rspInfo = new RspInfo() { state = false, msg = "Input data error!" };
            UInt160 nep5Hash;
            decimal value = 0;
            string txid = DbHelper.GetSendMoneyTxid(json);
            if (string.IsNullOrEmpty(txid))
            {
                if (json["coinType"].ToString() == "bct")
                    nep5Hash = UInt160.Parse(Config.getStrValue("bctHash"));
                else if (json["coinType"].ToString() == "bcp")
                    nep5Hash = UInt160.Parse(Config.getStrValue("bcpHash"));
                else
                    return rspInfo;

                value = Math.Round((decimal)json["value"] * (decimal)100000000.00000000, 0);
                UInt160 targetscripthash = ZoroHelper.GetPublicKeyHashFromAddress(json["address"].ToString());
                ScriptBuilder sb = new ScriptBuilder();

                KeyPair keypair = ZoroHelper.GetKeyPairFromWIF(Config.getStrValue("adminWif"));
                var adminHash = ZoroHelper.GetPublicKeyHashFromWIF(Config.getStrValue("adminWif"));

                sb.EmitSysCall("Zoro.NativeNEP5.Call", "Transfer", nep5Hash, adminHash, targetscripthash, new BigInteger(value));
                decimal gas = ZoroHelper.GetScriptGasConsumed(sb.ToArray(), "");
                InvocationTransaction tx = ZoroHelper.MakeTransaction(sb.ToArray(), keypair, Fixed8.FromDecimal(gas), Fixed8.One);
                var result = ZoroHelper.SendRawTransaction(tx.ToArray().ToHexString(), "");
                txid = tx.Hash.ToString();

                var state = (bool)(JObject.Parse(result)["result"]);
                if (state)
                {
                    rspInfo = new RspInfo()
                    {
                        state = true,
                        msg = txid 
                    };
                    DbHelper.SaveSendMoneyResult(json["coinType"].ToString(), json["key"].ToString(), txid, json["address"].ToString(), (decimal)json["value"]);
                }
                else
                {
                    rspInfo = new RspInfo()
                    {
                        state = false,
                        msg = result
                    };
                }

                return rspInfo;
            }
            else
            {
                rspInfo = new RspInfo()
                {
                    state = true,
                    msg = new TransResult()
                    { txid = txid, key = json["key"].ToString() }
                };
            }

            return rspInfo;
        }

        private static RspInfo GetInvokeRsp(string reqMethod, JObject json)
        {
            RspInfo rspInfo = new RspInfo();
            object resContent = null;
            switch (reqMethod)
            {
                case "getState":
                    resContent = GetStateRsp();
                    break;
                case "getBindNft":
                    resContent = GetBindNftRsp(json);
                    break;
                case "getNftInfo":
                    resContent = GetNftInfoRsp(json);
                    break;
                case "getUserNfts":
                    resContent = GetUserNftsRsp(json);
                    break;
                case "getApplicationLog":
                    var txid = json["txid"].ToString();
                    var method = json["method"].ToString();
                    resContent = GetApplicationLog(txid, method);
                    break;
            }
            rspInfo.state = true;
            rspInfo.msg = resContent;
            return rspInfo;
        }

        private static object GetApplicationLog(string txid, string method)
        {
            ApplicationLog applicationLog = new ApplicationLog();
            applicationLog.height = ZoroHelper.GetHeight();
            string url = Config.getStrValue("myApi") + $"?jsonrpc=2.0&id=1&method=getapplicationlog&params=['',\"{txid}\"]";
            var result = Helper.HttpGet(url);
            var executions = (JObject.Parse(result)["result"]["executions"] as JArray)[0] as JObject;
            var notificationsArray = executions["notifications"] as JArray;
            switch (method)
            {
                case "buy":
                    applicationLog.applicationLog = GetBuyLog(notificationsArray);
                    break;
                case "bind":
                    applicationLog.applicationLog = GetBindLog(notificationsArray[0] as JObject);
                    break;
                case "exchange":
                    applicationLog.applicationLog = GetExchange(notificationsArray[0] as JObject);
                    break;
                case "upgrade":
                    applicationLog.applicationLog = GetUpgradeLog(notificationsArray[0] as JObject);
                    break;
                case "addPoint":
                    applicationLog.applicationLog = GetAddPointLog(notificationsArray[0] as JObject);
                    break;
            }

            return applicationLog;
        }

        private static UpgradeLog GetUpgradeLog(JObject notification)
        {
            var upgradeLog = new UpgradeLog();
            var jValue = notification["state"]["value"] as JArray;
            upgradeLog.tokenId = jValue[1]["value"].ToString();
            upgradeLog.ownerAddress = Helper.GetJsonAddress((JObject)jValue[2]);
            upgradeLog.lastGrade= (int)jValue[3]["value"];
            upgradeLog.nowGrade = (int)jValue[4]["value"];
            return upgradeLog;
        }

        private static ExchangeLog GetExchange(JObject notification)
        {
            var exchangeLog = new ExchangeLog();
            var jValue = notification["state"]["value"] as JArray;
            exchangeLog.from = Helper.GetJsonAddress((JObject)jValue[1]);
            exchangeLog.to = Helper.GetJsonAddress((JObject)jValue[2]);
            exchangeLog.tokenId = jValue[3]["value"].ToString();
            return exchangeLog;
        }

        private static BindLog GetBindLog(JObject notification)
        {
            var bindLog = new BindLog();
            var jValue = notification["state"]["value"] as JArray;
            bindLog.ownerAddress = Helper.GetJsonAddress((JObject)jValue[2]);
            bindLog.tokenId= jValue[2]["value"].ToString();
            return bindLog;
        }

        private static BuyNftLog GetBuyLog(JArray notificationsArray)
        {
            var buyNftLog = new BuyNftLog();
            buyNftLog.addPointLogs = new List<AddPointLog>();
            buyNftLog.createNftLogs = new CreateNftLog();

            if (notificationsArray.Count == 3)
            {
                buyNftLog.addPointLogs.Add(GetAddPointLog(notificationsArray[0] as JObject));
                buyNftLog.addPointLogs.Add(GetAddPointLog(notificationsArray[1] as JObject));
                buyNftLog.createNftLogs = GetCreateLog(notificationsArray[2] as JObject);
            }
            if (notificationsArray.Count == 2)
            {
                buyNftLog.addPointLogs.Add(GetAddPointLog(notificationsArray[0] as JObject));
                buyNftLog.createNftLogs = GetCreateLog(notificationsArray[1] as JObject);
            }

            return buyNftLog;
        }

        private static AddPointLog GetAddPointLog(JObject notification)
        {
            var addPointLog = new AddPointLog();
            var jValue = notification["state"]["value"] as JArray;
            addPointLog.tokenId = jValue[1]["value"].ToString();
            addPointLog.ownerAddress = Helper.GetJsonAddress((JObject)jValue[2]);
            addPointLog.addPoint = Helper.GetJsonBigInteger((JObject)jValue[3]);

            return addPointLog;
        }

        private static CreateNftLog GetCreateLog(JObject notification)
        {
            var createLog = new CreateNftLog();
            var jValue = notification["state"]["value"] as JArray;
            //createLog.ownerAddress = Helper.GetJsonAddress((JObject)jValue[1]);
            //createLog.buyCount = Helper.GetJsonBigInteger((JObject)jValue[2]);
            //createLog.payValue = (long)Helper.GetJsonBigInteger((JObject)jValue[3]) / 100000000;

            createLog.tokenIdList = GetTokenList((JObject)jValue[4]);
            return createLog;
        }

        private static List<string> GetUserNftsRsp(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            var addr = ZoroHelper.GetParamBytes("(addr)" + json["address"].ToString());
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "getUserNfts", addr);
            var result = ZoroHelper.InvokeScript(sb.ToArray(), "");
            var stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;

            return GetTokenList(stack);
        }

        private static List<string> GetTokenList(JObject stack)
        {
            List<string> nftList = new List<string>();
            var value = stack["value"] as JArray;
            if (value.Count > 0)
            {
                foreach (var nft in value)
                {
                    nftList.Add(nft["key"]["value"].ToString());
                }
            }
            return nftList;
        }

        private static NFTInfo GetNftInfoRsp(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            var addr = ZoroHelper.GetParamBytes("(bytes)" + json["tokenId"].ToString());
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "getNftInfoById", addr);
            var result = ZoroHelper.InvokeScript(sb.ToArray(), "");
            var stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;

            var nftInfo = new NFTInfo();
            var value = stack["value"] as JArray;
            if (value == null || value.Count < 6)
                return nftInfo;
            if (value[0]["type"].ToString() == "ByteArray")
                nftInfo.TokenId = value[0]["value"].ToString();

            nftInfo.Owner = Helper.GetJsonAddress((JObject)value[1]);
            nftInfo.Rank = Helper.GetJsonInteger((JObject)value[2]);
            nftInfo.AllPoint = Helper.GetJsonInteger((JObject)value[3]);
            nftInfo.AvailablePoint = Helper.GetJsonInteger((JObject)value[4]);

            if (value[5]["type"].ToString() == "ByteArray")
                nftInfo.InviterTokenId = value[5]["value"].ToString();
            return nftInfo;
        }

        private static string GetBindNftRsp(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            var addr = ZoroHelper.GetParamBytes("(addr)" + json["address"].ToString());
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "getBindNft", addr);
            var result = ZoroHelper.InvokeScript(sb.ToArray(), "");
            var stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
            return stack["value"].ToString();
        }

        private static string GetStateRsp()
        {
            ScriptBuilder sb = new ScriptBuilder();
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "getState");
            var result = ZoroHelper.InvokeScript(sb.ToArray(), "");
            var stack = (JObject.Parse(result)["result"]["stack"] as JArray)[0] as JObject;
            return stack["value"].ToString();
        }

        private static ScriptBuilder ExchangeBulider(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            List<dynamic> paraList = new List<dynamic>();
            paraList.Add(ZoroHelper.GetParamBytes("(addr)" + json["from"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(addr)" + json["to"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(bytes)" + json["tokenId"].ToString()));

            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "exchange", paraList.ToArray());

            return sb;
        }

        private static ScriptBuilder UpgradeBuilder(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            List<dynamic> paraList = new List<dynamic>();
            paraList.Add(ZoroHelper.GetParamBytes("(hex160)" + Config.getStrValue("bctHash")));
            paraList.Add(ZoroHelper.GetParamBytes("(hex256)" + json["txid"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(bytes)" + json["tokenId"].ToString()));

            int nowGrade = int.Parse(json["nowGrade"].ToString());

            long receivableValue;
            int needPoint;

            GetUpgradeParams(out receivableValue, out needPoint, nowGrade);

            if (needPoint == 0) return sb;

            paraList.Add(ZoroHelper.GetParamBytes("(int)" + receivableValue.ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(int)" + needPoint.ToString()));

            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "upgrade", paraList.ToArray());

            return sb;
        }

        private static ScriptBuilder BuyBulider(JObject json, ref decimal gas)
        {
            ScriptBuilder sb = new ScriptBuilder();
            List<dynamic> paraList = new List<dynamic>();
            string inviterTokenId = json["inviterTokenId"].ToString();

            paraList.Add(ZoroHelper.GetParamBytes("(hex160)" + Config.getStrValue("bctHash")));
            paraList.Add(ZoroHelper.GetParamBytes("(hex256)" + json["txid"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(int)" + json["count"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(bytes)" + inviterTokenId));

            int count = int.Parse(json["count"].ToString());
            gas = 5 * count;
            long receivableValue = GetReceivableValue(count);

            paraList.Add(ZoroHelper.GetParamBytes("(int)" + receivableValue.ToString()));

            int pointValue = Config.getIntValue("silverPoint") * count;

            int twoLevelInviterPoint = pointValue * Config.getIntValue("twoLevelPercent") / 100;
            paraList.Add(ZoroHelper.GetParamBytes("(int)" + pointValue));
            paraList.Add(ZoroHelper.GetParamBytes("(int)" + twoLevelInviterPoint));
            paraList.Add(ZoroHelper.GetParamBytes("(int)" + 0));

            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "buy", paraList.ToArray());
            return sb;
        }

        private static ScriptBuilder BindBulider(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            List<dynamic> paraList = new List<dynamic>();
            paraList.Add(ZoroHelper.GetParamBytes("(addr)" + json["address"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(bytes)" + json["tokenId"].ToString()));
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "bind", paraList.ToArray());
            return sb;
        }

        private static ScriptBuilder ReducePointBuilder(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            List<dynamic> paraList = new List<dynamic>();
            paraList.Add(ZoroHelper.GetParamBytes("(bytes)" + json["tokenId"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(int)" + "-" + json["pointValue"].ToString()));
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "addPoint", paraList.ToArray());
            return sb;
        }

        private static ScriptBuilder ReduceGradeBuilder(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "reduceGrade", ZoroHelper.GetParamBytes("(bytes)" + json["tokenId"].ToString()));
            return sb;
        }

        private static ScriptBuilder AddPointBuilder(JObject json)
        {
            ScriptBuilder sb = new ScriptBuilder();
            List<dynamic> paraList = new List<dynamic>();
            paraList.Add(ZoroHelper.GetParamBytes("(bytes)" + json["tokenId"].ToString()));
            paraList.Add(ZoroHelper.GetParamBytes("(int)" + Config.getIntValue("memberPoint")));
            sb.EmitAppCall(UInt160.Parse(Config.getStrValue("nftHash")), "addPoint", paraList.ToArray());
            return sb;
        }

        private static void GetUpgradeParams(out long receivableValue, out int needPoint, int nowGrade)
        {
            if (nowGrade == 1)
            {
                receivableValue = Config.getLongValue("goldPrice");
                needPoint = Config.getIntValue("goldUpgradePoint");
            }

            else if (nowGrade == 2)
            {
                receivableValue = Config.getLongValue("platinumPrice");
                needPoint = Config.getIntValue("platinumUpgradePoint");
            }

            else if (nowGrade == 3)
            {
                receivableValue = Config.getLongValue("diamondPrice");
                needPoint = Config.getIntValue("diamondUpgradePoint");
            }

            else
            {
                receivableValue = 0;
                needPoint = 0;
            }
        }

        private static long GetReceivableValue(int count)
        {
            long receivableValue =Config.getLongValue("silverPrice") * count;
            if (count > Config.getIntValue("oneDiscountCount"))
            {
                if (count > Config.getIntValue("twoDiscountCount"))
                    receivableValue = receivableValue * Config.getIntValue("twoDiscountPercent") / 100;
                else
                    receivableValue = receivableValue * Config.getIntValue("oneDiscountPercent") / 100;
            }

            return receivableValue;
        }

    }
}
