﻿using Neo.VM;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Windows.Forms;
using Zoro;
using Zoro.Ledger;
using Zoro.SmartContract;
using Zoro.Wallets;

namespace Zoro_Gui
{
    public partial class FrmZoroGui : Form
    {
        private byte[] contractScript;
        private KeyPair keypair;
        private UInt160 addressHash;
        decimal bcpFee = 100;

        public FrmZoroGui()
        {
            InitializeComponent();
        }

        private void tbxContractPath_TextChanged(object sender, EventArgs e)
        {
            GetContract();
        }

        private void btnPublish_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(tbxWif.Text))
            {
                MessageBox.Show("请输入钱包 wif ！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(tbxContractPath.Text))
            {
                MessageBox.Show("请输入合约文件！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            keypair = ZoroHelper.GetKeyPairFromWIF(tbxWif.Text);

            byte[] parameter__list = ZoroHelper.HexString2Bytes(tbxParameterType.Text);
            byte[] return_type = ZoroHelper.HexString2Bytes("05");
            int need_storage = cbxNeedStorge.Checked == true ? 1 : 0;
            int need_nep4 = cbxNeedNep4.Checked == true ? 2 : 0;
            int need_canCharge = cbxNeedCharge.Checked == true ? 4 : 0;

            using (ScriptBuilder sb = new ScriptBuilder())
            {
                var ss = need_storage | need_nep4 | need_canCharge;
                sb.EmitPush(tbxDescri.Text);
                sb.EmitPush(tbxEmail.Text);
                sb.EmitPush(tbxAuthor.Text);
                sb.EmitPush(tbxVersion.Text);
                sb.EmitPush(tbxContractName.Text);
                sb.EmitPush(ss);
                sb.EmitPush(return_type);
                sb.EmitPush(parameter__list);
                sb.EmitPush(contractScript);
                sb.EmitSysCall("Zoro.Contract.Create");

                bcpFee = ZoroHelper.GetScriptGasConsumed(sb.ToArray(), "");

                lblBcpFee.Text = bcpFee.ToString();

                var result = ZoroHelper.SendInvocationTransaction(sb.ToArray(), keypair, "", Fixed8.FromDecimal(bcpFee), Fixed8.One);

                JObject resJO = JObject.Parse(result);
                MessageBox.Show(resJO.ToString(), "交易返回", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void btnInvoke_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(tbxContractScriptHash.Text))
            {
                MessageBox.Show("合约 Hash 不能为空！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(tbxMethodName.Text))
            {
                MessageBox.Show("调用接口不能为空！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ScriptBuilder sb = new ScriptBuilder();

            if (!string.IsNullOrEmpty(rtbxParameterJson.Text))
            {
                var parameterArray = rtbxParameterJson.Text.Split(';');
                sb.EmitAppCall(ZoroHelper.Parse(tbxContractScriptHash.Text), tbxMethodName.Text, parameterArray);
            }
            else
            {
                sb.EmitAppCall(ZoroHelper.Parse(tbxContractScriptHash.Text), tbxMethodName.Text);
            }

            var info = ZoroHelper.InvokeScript(sb.ToArray(), "");

            rtbxReturnJson.Text = info;
        }

        private void btnSendRaw_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(tbxWif.Text))
            {
                MessageBox.Show("请输入钱包 wif ！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(tbxContractScriptHash.Text))
            {
                MessageBox.Show("合约 Hash 不能为空！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(rtbxParameterJson.Text))
            {
                MessageBox.Show("调用参数不能为空！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

        }

        private void GetAccount()
        {
            try
            {
                keypair = ZoroHelper.GetKeyPairFromWIF(tbxWif.Text);
                addressHash = ZoroHelper.GetPublicKeyHashFromWIF(tbxWif.Text);
                tbxAddress.Text = ZoroHelper.GetAddressFromScriptHash(addressHash);
            }
            catch
            {
                MessageBox.Show("Wif 密钥格式错误！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        private void GetContract()
        {
            var contractPath = tbxContractPath.Text;
            tbxContractName.Text = contractPath.Replace(".avm", "");
            if (!System.IO.File.Exists(contractPath))
            {
                MessageBox.Show("合约文件路径无效！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            contractScript = System.IO.File.ReadAllBytes(contractPath);
            var contractHash = contractScript.ToScriptHash();
            tbxContractHash.Text = contractHash.ToString();
        }

        private void tbxWif_TextChanged(object sender, EventArgs e)
        {
            GetAccount();
            GetBalance();
        }

        private void GetBalance()
        {
            UInt160 bcpAssetId = Genesis.BcpContractAddress;
            UInt160 bctAssetId = Genesis.BctContractAddress;

            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitSysCall("Zoro.NativeNEP5.Call", "BalanceOf", bcpAssetId, addressHash);
                sb.EmitSysCall("Zoro.NativeNEP5.Call", "Decimals", bcpAssetId);

                var info = ZoroHelper.InvokeScript(sb.ToArray(), "");
                var value = GetBalanceFromJson(info);

                lblBcpBalance.Text = value;

            }

            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitSysCall("Zoro.NativeNEP5.Call", "BalanceOf", bctAssetId, tbxAddress.Text);
                sb.EmitSysCall("Zoro.NativeNEP5.Call", "Decimals", bctAssetId);

                var info = ZoroHelper.InvokeScript(sb.ToArray(), "");
                var value = GetBalanceFromJson(info);

                lblBctBalance.Text = value;

            }
        }

        string GetBalanceFromJson(string info)
        {
            string result = "";
            JObject json = JObject.Parse(info);

            if (json.ContainsKey("result"))
            {
                JObject json_result = json["result"] as JObject;
                JArray stack = json_result["stack"] as JArray;

                if (stack != null && stack.Count >= 2)
                {
                    string balance = ZoroHelper.GetJsonValue(stack[0] as JObject);
                    string decimals = ZoroHelper.GetJsonValue(stack[1] as JObject);

                    Decimal value = Decimal.Parse(balance) / new Decimal(Math.Pow(10, int.Parse(decimals)));
                    string fmt = "{0:N" + decimals + "}";
                    result = string.Format(fmt, value);
                }
            }
            else if (json.ContainsKey("error"))
            {
                JObject json_error_obj = json["error"] as JObject;
                result = json_error_obj.ToString();
            }

            return result;
        }

        private void cbxNeedNep4_CheckedChanged(object sender, EventArgs e)
        {
            if (cbxNeedNep4.CheckState == CheckState.Checked)
                bcpFee += (decimal)500;
            else
                bcpFee -= (decimal)500;
            lblBcpFee.Text = bcpFee.ToString();
        }

        private void cbxNeedStorge_CheckedChanged(object sender, EventArgs e)
        {
            if (cbxNeedStorge.CheckState == CheckState.Checked)
                bcpFee += (decimal)400;
            else
                bcpFee -= (decimal)400;
            lblBcpFee.Text = bcpFee.ToString();
        }

        private void btnSendTransaction_Click(object sender, EventArgs e)
        {
            UInt160 assetId;
            if (cmbxTokenType.Text == "BCP")
            {
                assetId = Genesis.BcpContractAddress;
            }
            else if (cmbxTokenType.Text == "BCT")
            {
                assetId = Genesis.BctContractAddress;
            }
            else
            {
                MessageBox.Show("请选择币种！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(tbxValue.Text))
            {
                MessageBox.Show("请输入金额！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(tbxTargetAddress.Text))
            {
                MessageBox.Show("请输入接收地址！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Decimal value = Decimal.Parse(tbxValue.Text, NumberStyles.Float) * new Decimal(Math.Pow(10, 8));
            UInt160 targetscripthash1 = ZoroHelper.GetPublicKeyHashFromAddress(tbxAddress.Text);
            UInt160 targetscripthash = ZoroHelper.GetPublicKeyHashFromAddress(tbxTargetAddress.Text);

            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitSysCall("Zoro.NativeNEP5.Call", "Transfer", assetId, addressHash, targetscripthash, value);

                decimal gas = ZoroHelper.GetScriptGasConsumed(sb.ToArray(), "");

                var result = ZoroHelper.SendInvocationTransaction(sb.ToArray(), keypair, "", Fixed8.FromDecimal(gas), Fixed8.One);

                rtbxTranResult.Text = result;
            }
        }

        private void FrmZoroGui_Load(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(tbxWif.Text))
            {
                GetAccount();
            }

            if (!string.IsNullOrEmpty(tbxContractPath.Text))
            {
                GetContract();
            }

            lblBcpFee.Text = bcpFee.ToString();

            cmbxTokenType.SelectedIndex = 0;
        }
    }
}