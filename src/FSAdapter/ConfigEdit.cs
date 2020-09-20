﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Xml;
using System.Xml.Schema;

namespace FSAdapter
{
	// All API here use XPath Select to find node from xml.
	public partial class FSAdapter
	{
		//CTX is short term of "Context"
		private static readonly string Conference_Ctx = "default";
		private static readonly string Dialplan_SIP = "sip_uri";
		private static readonly string Dialplan_Conf = "sip_conference";
		//dialplan的預設Context名稱，很多地方會引用到
		private static readonly string Dialplan_Ctx = "sip-dialplan";
		private static readonly string DialplanFile = Dialplan_Ctx + ".xml";
		//ACL預設list名稱
		private static readonly string AclListName = "sip-acl";

		private static readonly string CallGroup = "sip";
		private static readonly string UserFolder = CallGroup;
		private static readonly string UserCfgFile = CallGroup + ".xml";
		private static readonly string GlobalVarsFile = "vars.xml";

		//  vars.xml內的資料樣本
		//	<X-PRE-PROCESS cmd="set" data="force_local_ip_v4=172.16.65.145"/>
		//  <X-PRE-PROCESS cmd="set" data="local_ip_v4=$${force_local_ip_v4}"/>
		//FreeSwitch不能直接改 local_ip_v4 變數，它每分鐘會refresh一次
		//要用這種方法去強迫它變更，這API必須用在FreeSwitch Server有多IP或多網卡的狀況。
		//如果不強迫指定，它只會抓OS回報的第一個IP(順序不定)
		public void SetFreeSwitchIP(string ip)
		{
			string path = GetServiceBinPath() + "\\conf";
			string file = path + "\\" + GlobalVarsFile;

			//用XPath找 include下的X-PRE-PROCESS 節點，並且該節點attribute為 cmd='set' 與 data='local_ip_v4=$${force_local_ip_v4}'
			//資料樣本  <X-PRE-PROCESS cmd="set" data="local_ip_v4=$${force_local_ip_v4}"/>
			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = "";
			XmlNode node = null;
			XmlNode child = null;
			XmlAttribute attribute = null;

			//20190804 Roy: 這邊有個詭異的Bug，原本以為變數設定不需考慮順序，但其實必須照順序來設
			//如果在XML檔先寫  local_ip_v4=$${force_local_ip_v4} 這行，後面才寫 force_local_ip_v4=192.168.20.18
			//這樣會造成sofia(sip module)錯亂，不是無法啟動就是直接跑去執行 external profile.....
			xpath = "/include/X-PRE-PROCESS[@cmd='set'][contains(@data, 'force_local_ip_v4=')]";
			node = doc.SelectSingleNode(xpath);
			if (node == null)
			{
				Log.DebugFormat("node force_local_ip_v4 not found, add it");

				child = doc.CreateNode(XmlNodeType.Element, "X-PRE-PROCESS", null);
				attribute = doc.CreateAttribute("cmd");
				attribute.Value = "set";
				child.Attributes.Append(attribute);

				attribute = doc.CreateAttribute("data");
				attribute.Value = string.Format("force_local_ip_v4={0}", ip);
				child.Attributes.Append(attribute);

				xpath = "/include";
				node = doc.SelectSingleNode(xpath);
				node.AppendChild(child);
			}
			else
			{
				Log.DebugFormat("node force_local_ip_v4 modify to {0}", ip);
				node.Attributes["data"].Value = string.Format("force_local_ip_v4={0}", ip);
			}

			xpath = "/include/X-PRE-PROCESS[@cmd='set'][@data='local_ip_v4=$${force_local_ip_v4}']";
			node = doc.SelectSingleNode(xpath);
			if (null == node)
			{
				//create node
				child = doc.CreateNode(XmlNodeType.Element, "X-PRE-PROCESS", null);
				attribute = doc.CreateAttribute("cmd");
				attribute.Value = "set";
				child.Attributes.Append(attribute);

				attribute = doc.CreateAttribute("data");
				attribute.Value = "local_ip_v4=$${force_local_ip_v4}";
				child.Attributes.Append(attribute);

				xpath = "/include";
				node = doc.SelectSingleNode(xpath);
				node.AppendChild(child);
			}
			else
				Log.ErrorFormat("local_ip_v4=$${force_local_ip_v4} exists, skip add node");

			XMLUtils.SaveXML(doc, file);
		}
		public void ResetFreeSwitchIP()
		{
			string path = GetServiceBinPath() + "\\conf";
			string file = path + "\\" + GlobalVarsFile;

			//用XPath找 include下的X-PRE-PROCESS 節點，並且該節點attribute為 cmd='set' 與 data='local_ip_v4=$${force_local_ip_v4}'
			//資料樣本  <X-PRE-PROCESS cmd="set" data="local_ip_v4=$${force_local_ip_v4}"/>
			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = "/include/X-PRE-PROCESS[@cmd='set'][@data='local_ip_v4=$${force_local_ip_v4}']";
			XmlNode node = doc.SelectSingleNode(xpath);

			if (null != node)
			{
				node.ParentNode.RemoveChild(node);
			}

			xpath = "/include/X-PRE-PROCESS[@cmd='set'][contains(@data, 'local_ip_v4=')]";
			node = doc.SelectSingleNode(xpath);
			if (node != null)
			{
				node.ParentNode.RemoveChild(node);
			}
			XMLUtils.SaveXML(doc, file);
		}

		//為了安全起見，目前所有設定調整都限定只能對本機(localhost)的FreeSwitch處理
		//這API是幹掉FreeSwitch所有預設dialplan與User，並且把sip profile裡面的
		//context與inbound acl指定給預設名稱。
		//因為FreeSwitch設定太多太複雜，所以弄了這個API。但注意這API只會修改internal sip profile。
		//如果要設定external profile，使用者要自己呼叫SetSipProfileExternal()去綁定正確的context與ACL。
		//(dialplan與user account還是可以沿用)
		public void ResetDefaultConfig()
		{
			ResetDialplan();
			ResetSipUser();
			ResetACL();
			//ResetFreeSwitchIP();
			SetSipProfileInternal(Dialplan_Ctx, AclListName);
		}

		//清掉指定的Conference設定，重新填回預設值
		public void ResetConferenceConfig()
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "conference.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = string.Format("/configuration/profiles/profile[@name='{0}']", Conference_Ctx);
			XmlNode node = doc.SelectSingleNode(xpath);
			XmlNode parent = null;
			XmlNode child = null;
			Dictionary<string, string> attr_map = new Dictionary<string, string>();

			if (node != null)
			{
				parent = node.ParentNode;
				parent.RemoveChild(node);
			}

			attr_map["name"] = Conference_Ctx;
			child = XMLUtils.CreateElementNode(doc, "profile", attr_map);
			parent.AppendChild(child);
			
			//切換到 <profile>
			parent = child;
			attr_map.Clear();
			attr_map["name"] = "domain";
			attr_map["value"] = "$${domain}";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);
			attr_map.Clear();
			attr_map["name"] = "rate";
			attr_map["value"] = "8000";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["name"] = "interval";
			attr_map["value"] = "20";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);
			attr_map.Clear();
			attr_map["name"] = "energy-level";
			attr_map["value"] = "100";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);
			attr_map.Clear();
			attr_map["name"] = "caller-id-name";
			attr_map["value"] = "$${outbound_caller_name}";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);
			attr_map.Clear();
			attr_map["name"] = "caller-id-number";
			attr_map["value"] = "$${outbound_caller_id}";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);
			attr_map.Clear();
			attr_map["name"] = "comfort-noise";
			attr_map["value"] = "true";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);
			attr_map["name"] = "auto-gain-level";
			attr_map["value"] = "true";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);

			XMLUtils.SaveXML(doc, file);
		}

		//SMS設定 (SIP Message)
		public void EnableSipMessage()
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "sms.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = "/configuration/modules/load[@module='mod_sms']";
			XmlNode node = doc.SelectSingleNode(xpath);

			if (node == null)
			{
				xpath = "/configuration/modules";
				node = doc.SelectSingleNode(xpath);

				XmlNode new_node = doc.CreateNode(XmlNodeType.Element, "load", null);
				XmlAttribute attribute = doc.CreateAttribute("module");
				attribute.Value = "mod_sms";
				new_node.Attributes.Append(attribute);
				new_node.Attributes.Append(attribute);

				node.AppendChild(new_node);
				XMLUtils.SaveXML(doc, file);
			}
			else
				Log.ErrorFormat("node [{0}] not found, skip...", xpath);
		}
		public void DisableSipMessage()
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "sms.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = "/configuration/modules/load[@module='mod_sms']";
			XmlNode node = doc.SelectSingleNode(xpath);

			if (node != null)
			{
				node.ParentNode.RemoveChild(node);
				XMLUtils.SaveXML(doc, file);
			}
			else
				Log.ErrorFormat("node [{0}] not found, skip...", xpath);
		}

		//清空Dialplan設定檔，重建預設的設定(但各個dialplan需要自己加)
		public void ResetDialplan()
		{
			string path = GetServiceBinPath() + "\\conf\\dialplan";
			string file = path + "\\" + DialplanFile;
			
			Utils.DeleteAll(path);
			File.Delete(file);

			Dictionary<string, string> attr_map = new Dictionary<string, string>();
			XmlDocument doc = new XmlDocument();
			XmlNode parent = doc.CreateNode(XmlNodeType.Element, "include", null);
			XmlNode child = null;
			doc.AppendChild(parent);

			attr_map.Clear();
			attr_map["name"] = Dialplan_Ctx;
			child = XMLUtils.CreateElementNode(doc, "context", attr_map);
			parent.AppendChild(child);

			//切換到 <context>
			parent = child;
			attr_map.Clear();
			attr_map["name"] = "unloop";
			child = XMLUtils.CreateElementNode(doc, "extension", attr_map);
			parent.AppendChild(child);

			//切換到<extension>
			parent = child;
			attr_map.Clear();
			attr_map["field"] = "${unroll_loops}";
			attr_map["expression"] = "^true$";
			child = XMLUtils.CreateElementNode(doc, "condition", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["field"] = "${sip_looped_call}";
			attr_map["expression"] = "^true$";
			child = XMLUtils.CreateElementNode(doc, "condition", attr_map);
			parent.AppendChild(child);

			//切換到<condition>
			parent = child;
			attr_map.Clear();
			attr_map["application"] = "deflect";
			attr_map["data"] = "${destination_number}";
			child = XMLUtils.CreateElementNode(doc, "action", attr_map);
			parent.AppendChild(child);

			//切換回 <context>
			parent = parent.ParentNode.ParentNode;
			attr_map.Clear();
			attr_map["cmd"] = "include";
			attr_map["data"] = string.Format("{0}/*.xml", Dialplan_Ctx);
			child = XMLUtils.CreateElementNode(doc, "X-PRE-PROCESS", attr_map);
			parent.AppendChild(child);

			XMLUtils.SaveXML(doc, file);
			Directory.CreateDirectory(path + "\\" + Dialplan_Ctx);
		}

		public void SetSipDialplan(string regex)
		{
			string path = string.Format("{0}\\conf\\dialplan\\{1}",GetServiceBinPath(), Dialplan_Ctx);
			string file = string.Format("{0}\\{1}.xml", path, Dialplan_SIP);

			Log.DebugFormat("SetSipDialplan = {0}", regex);

			//先處理regex：
			//1.頭尾要加上^跟$
			//2.要加大括號，不然設定裡那個 ${sip_id} 變數會拉不到資料
			//範例： "sip-\d{4}" => 處理後變成 "^(sip-\d{4})$"
			if (regex[0] == '^')
				regex = regex.Substring(1);
			if (regex[regex.Length-1] == '$')
				regex = regex.Substring(0, regex.Length - 2);

			regex = string.Format("^({0})$", regex);

			if (File.Exists(file))
				File.Delete(file);

			Dictionary<string, string> attr_map = new Dictionary<string, string>();
			XmlDocument doc = new XmlDocument();
			XmlNode child = null;
			XmlNode parent = doc.CreateNode(XmlNodeType.Element, "include", null);
			doc.AppendChild(parent);

			attr_map["name"] = Dialplan_SIP;
			child = XMLUtils.CreateElementNode(doc, "extension", attr_map);
			parent.AppendChild(child);

			//切換到 <extension>
			parent = child;
			attr_map.Clear();
			attr_map["field"] = "destination_number";
			attr_map["expression"] = regex;
			child = XMLUtils.CreateElementNode(doc, "condition", attr_map);
			parent.AppendChild(child);

			//切換到 <condition>
			parent = child;
			attr_map.Clear();
			attr_map["application"] = "set";
			attr_map["data"] = "sip_id=$1";
			child = XMLUtils.CreateElementNode(doc, "action", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["application"] = "bridge";
			attr_map["data"] = "user/${sip_id}@${domain_name}";
			child = XMLUtils.CreateElementNode(doc, "action", attr_map);
			parent.AppendChild(child);

			XMLUtils.SaveXML(doc, file);
		}

		//注意：regex字串最外層一定要有個() 把整個ID包起來
		//      不然會撥不通...因為裡面dest_id=$1 會抓不到
		public void SetConferenceDialplan(string regex)
		{
			string path = string.Format("{0}\\conf\\dialplan\\{1}", GetServiceBinPath(), Dialplan_Ctx);
			string file = string.Format("{0}\\{1}.xml", path, Dialplan_Conf);

			Log.DebugFormat("SetConferenceDialplan = {0}", regex);

			//先處理regex：
			//1.頭尾要加上^跟$
			//2.要加大括號，不然設定裡那個 ${dest_id} 變數會拉不到資料
			//範例： "conf-\d{4}" => 處理後變成 "^(conf-\d{4})$"
			if (regex[0] == '^')
				regex = regex.Substring(1);
			if (regex[regex.Length - 1] == '$')
				regex = regex.Substring(0, regex.Length - 2);

			regex = string.Format("^({0})$", regex);

			if (File.Exists(file))
				File.Delete(file);

			Dictionary<string, string> attr_map = new Dictionary<string, string>();
			XmlDocument doc = new XmlDocument();
			XmlNode child = null;
			XmlNode parent = doc.CreateNode(XmlNodeType.Element, "include", null);
			doc.AppendChild(parent);

			attr_map["name"] = Dialplan_SIP;
			child = XMLUtils.CreateElementNode(doc, "extension", attr_map);
			parent.AppendChild(child);

			//切換到 <extension>
			parent = child;
			attr_map.Clear();
			attr_map["field"] = "destination_number";
			attr_map["expression"] = regex;
			child = XMLUtils.CreateElementNode(doc, "condition", attr_map);
			parent.AppendChild(child);

			//切換到 <condition>
			parent = child;
			attr_map.Clear();
			attr_map["application"] = "set";
			attr_map["data"] = "dest_id=$1";
			child = XMLUtils.CreateElementNode(doc, "action", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["application"] = "answer";
			child = XMLUtils.CreateElementNode(doc, "action", attr_map);
			parent.AppendChild(child);

			//<action application="conference" data="${dest_id}@sip-conf"/>
			attr_map.Clear();
			attr_map["application"] = "conference";
			attr_map["data"] = "${dest_id}@" + Conference_Ctx;
			child = XMLUtils.CreateElementNode(doc, "action", attr_map);
			parent.AppendChild(child);

			XMLUtils.SaveXML(doc, file);
		}

		//FreeSwitch的SIP Profile預設分為internal與external，因為欄位太多太複雜所以不做清除，只做修改。
		//裡面的 <param name='context' value='xxxx'> 欄位 xxxx要指定 dialplan名稱，
		//      例如<param name="context" value="sip_call"/>
		//  <param name="apply-inbound-acl" value="xxxxx"/> 欄位　xxxxx要指定ACL list 名稱，
		//      例如 <param name="apply-inbound-acl" value="domains"/>
		public void SetSipProfileInternal(string dialplan, string acl_list)
		{
			string path = GetServiceBinPath() + "\\conf\\sip_profiles";
			string file = path + "\\" + "internal.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = "/profile/settings/param[@name='context']";
			XmlNode node = doc.SelectSingleNode(xpath);
			XmlAttribute attribute = null;
			XmlNode parent = null;

			if (node != null)
			{
				node.Attributes["value"].Value = dialplan;
			}
			else
			{
				xpath = "/profile/settings";
				parent = doc.SelectSingleNode(xpath);
				XmlNode child = doc.CreateNode(XmlNodeType.Element, "param", null);
				attribute = doc.CreateAttribute("name");
				attribute.Value = "context";
				child.Attributes.Append(attribute);
				attribute = doc.CreateAttribute("value");
				attribute.Value = dialplan;
				child.Attributes.Append(attribute);

				parent.AppendChild(child);
			}

			//綁定ACL，不然預設是會reject incoming connection
			//注意ACL那邊也要設定好同名的ACL list，並加入allow ip range(CIDR)
			xpath = "/profile/settings/param[@name='apply-inbound-acl']";
			node = doc.SelectSingleNode(xpath);

			if (node == null)
			{
				//這個inbound acl好像預設是關掉的...所以要檢查是否存在。
				node = doc.CreateNode(XmlNodeType.Element, "param", null);
				attribute = doc.CreateAttribute("name");
				attribute.Value = "apply-inbound-acl";
				node.Attributes.Append(attribute);

				attribute = doc.CreateAttribute("value");
				attribute.Value = acl_list;
				node.Attributes.Append(attribute);

				xpath = "/profile/settings";
				parent = doc.SelectSingleNode(xpath);
				parent.AppendChild(node);
			}
			else
				node.Attributes["value"].Value = acl_list;

			XMLUtils.SaveXML(doc, file);
		}

		//清空Sip User設定
		public void ResetSipUser(bool clear_all = true)
		{
			string path = GetServiceBinPath() + "\\conf\\directory";
			string file = path + "\\" + UserCfgFile;
			if(clear_all)
				Utils.DeleteAll(path);
			else
			{
				File.Delete(file);
			}

			//以下建立基礎profile of sip user....(指定要include哪個目錄的account)
			Dictionary<string, string> attr_map = new Dictionary<string, string>();
			XmlDocument doc = new XmlDocument();
			XmlNode parent = doc.CreateNode(XmlNodeType.Element, "include", null);
			doc.AppendChild(parent);

			attr_map.Clear();
			attr_map["name"] = "$${domain}";
			XmlNode child = XMLUtils.CreateElementNode(doc, "domain", attr_map);
			parent.AppendChild(child);

			//現在parent變成 <domain>
			parent = child;
			child = doc.CreateNode(XmlNodeType.Element, "params", null);
			parent.AppendChild(child);

			//現在parent變成 <params>
			parent = child;
			attr_map.Clear();
			attr_map["name"] = "dial-string";
			attr_map["value"] = "{^^:sip_invite_domain=${dialed_domain}:presence_id=${dialed_user}@${dialed_domain}}${sofia_contact(*/${dialed_user}@${dialed_domain})},${verto_contact(${dialed_user}@${dialed_domain})}";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["name"] = "jsonrpc-allowed-methods";
			attr_map["value"] = "verto";
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);

			//回上一層 <domain>
			parent = parent.ParentNode;
			child = doc.CreateNode(XmlNodeType.Element, "variables", null);
			parent.AppendChild(child);

			//現在parent變成 <variables>
			parent = child;
			attr_map.Clear();
			attr_map["name"] = "record_stereo";
			attr_map["value"] = "false";
			child = XMLUtils.CreateElementNode(doc, "variable", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["name"] = "transfer_fallback_extension";
			attr_map["value"] = "operator";
			child = XMLUtils.CreateElementNode(doc, "variable", attr_map);
			parent.AppendChild(child);

			//回上一層 <domain>
			parent = parent.ParentNode;
			child = doc.CreateNode(XmlNodeType.Element, "groups", null);
			parent.AppendChild(child);

			//現在parent變成 <groups>
			parent = child;
			attr_map["name"] = "sip";
			child = XMLUtils.CreateElementNode(doc, "group", attr_map);
			parent.AppendChild(child);

			//現在parent變成 <group>
			parent = child;
			child = doc.CreateNode(XmlNodeType.Element, "users", null);
			parent.AppendChild(child);

			//現在parent變成 <users>
			parent = child;
			attr_map["cmd"] = "include";
			attr_map["data"] = string.Format("{0}/*.xml", UserFolder);
			child = XMLUtils.CreateElementNode(doc, "X-PRE-PROCESS", attr_map);
			parent.AppendChild(child);

			//收工
			XMLUtils.SaveXML(doc, file);
			Directory.CreateDirectory(path + "\\" + UserFolder);
		}
		//增加一個SIP User (注意User ID不能重複)
		public void AddSipUser(string id, string pwd)
		{
			string path = GetServiceBinPath() + "\\conf\\directory\\" + UserFolder;
			string file = string.Format("{0}\\{1}.xml", path, id);
			
			Dictionary<string, string> attr_map = new Dictionary<string, string>();
			XmlDocument doc = new XmlDocument();
			XmlNode child = null;
			XmlNode parent = doc.CreateNode(XmlNodeType.Element, "include", null);
			doc.AppendChild(parent);

			attr_map["id"] = id;
			child = XMLUtils.CreateElementNode(doc, "user", attr_map);
			parent.AppendChild(child);

			//切換到<user>
			parent = child;
			child = doc.CreateNode(XmlNodeType.Element, "params", null);
			parent.AppendChild(child);

			//切換到<params>
			parent = child;
			attr_map.Clear();
			attr_map["name"] = "password";
			attr_map["value"] = pwd;
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);
			attr_map.Clear();
			attr_map["name"] = "vm-password";
			attr_map["value"] = pwd;
			child = XMLUtils.CreateElementNode(doc, "param", attr_map);
			parent.AppendChild(child);

			//回到<user>
			parent = parent.ParentNode;
			child = doc.CreateNode(XmlNodeType.Element, "variables", null);
			parent.AppendChild(child);

			//切換到 <variables>
			parent = child;
			attr_map.Clear();
			attr_map["name"] = "toll_allow";
			attr_map["value"] = "sip-call";
			child = XMLUtils.CreateElementNode(doc, "variable", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["name"] = "user_context";
			attr_map["value"] = Dialplan_Ctx;
			child = XMLUtils.CreateElementNode(doc, "variable", attr_map);
			parent.AppendChild(child);

			attr_map.Clear();
			attr_map["name"] = "effective_caller_id_name";
			attr_map["value"] = string.Format("Extension {0}", id);
			child = XMLUtils.CreateElementNode(doc, "variable", attr_map);
			parent.AppendChild(child);
			
			attr_map.Clear();
			attr_map["name"] = "effective_caller_id_number";
			attr_map["value"] = id;
			child = XMLUtils.CreateElementNode(doc, "variable", attr_map);
			parent.AppendChild(child);
			attr_map.Clear();
			attr_map["name"] = "callgroup";
			attr_map["value"] = CallGroup;
			child = XMLUtils.CreateElementNode(doc, "variable", attr_map);
			parent.AppendChild(child);

			XMLUtils.SaveXML(doc, file);
		}
		//幹掉指定的SIP User
		public void RemoveSipUser(string id)
		{
			string path = GetServiceBinPath() + "\\conf\\directory\\" + UserFolder;
			string file = string.Format("{0}\\{1}.xml", path, id);
			if (File.Exists(file))
				File.Delete(file);
		}

		//在指定的ACL List裡增加幾個allow與deny的 node  (List裡的 string必須為CIDR格式)
		public void AddACL(List<string> allow_list, List<string> deny_list)
		{ AddACL(AclListName, allow_list, deny_list); }
		//在指定的ACL List裡增加幾個allow與deny的 node  (List裡的 string必須為CIDR格式)
		public void AddACL(string list_name, List<string> allow_list, List<string> deny_list)
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "acl.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = string.Format("/configuration/network-lists/list[@name='{0}']", list_name);
			XmlNode acl_list = null;
			XmlNode node = null;
			acl_list = doc.SelectSingleNode(xpath);

			//去找名為 %list_name 的ACL List，如果沒找到就不做事了....
			if (acl_list == null)
				return;
			foreach (var cidr_ip in allow_list)
			{
				//每個ACL node 放在 configuration/network-lists/list 下
				//範例： <node type="allow" cidr="192.168.0.0/16" />

				XmlNode new_node = doc.CreateNode(XmlNodeType.Element, "node", null);
				XmlAttribute attribute = doc.CreateAttribute("cidr");
				attribute.Value = cidr_ip;
				new_node.Attributes.Append(attribute);

				attribute = doc.CreateAttribute("type");
				attribute.Value = FS_ACL.allow.ToString();
				new_node.Attributes.Append(attribute);

				acl_list.AppendChild(new_node);
			}

			foreach (var cidr_ip in deny_list)
			{
				//每個ACL node 放在 configuration/network-lists/list 下
				//範例： <node type="allow" cidr="192.168.0.0/16" />

				XmlNode new_node = doc.CreateNode(XmlNodeType.Element, "node", null);
				XmlAttribute attribute = doc.CreateAttribute("cidr");
				attribute.Value = cidr_ip;
				new_node.Attributes.Append(attribute);

				attribute = doc.CreateAttribute("type");
				attribute.Value = FS_ACL.deny.ToString();
				new_node.Attributes.Append(attribute);

				acl_list.AppendChild(new_node);
			}

			XMLUtils.SaveXML(doc, file);
		}
		//在指定的ACL List裡增加一個node (cidr_ip參數必須為CIDR格式)
		public void AddACL(string cidr_ip, FSW_ACL action)
		{ AddACL(AclListName, cidr_ip, action); }
		public void AddACL(string list_name, string cidr_ip, FSW_ACL action)
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "acl.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = string.Format("/configuration/network-lists/list[@name='{0}']", list_name);
			XmlNode node = doc.SelectSingleNode(xpath);
			//只找現有的ACL_List來增加 acl node....沒找到就不做了
			//ACL node範例  <node type="allow" cidr="192.168.0.0/16" />
			if (node != null)
			{
				XmlNode new_node = doc.CreateNode(XmlNodeType.Element, "node", null);
				XmlAttribute attribute = doc.CreateAttribute("cidr");
				attribute.Value = cidr_ip;
				new_node.Attributes.Append(attribute);
				
				attribute = doc.CreateAttribute("type");
				attribute.Value = action.ToString(); 
				new_node.Attributes.Append(attribute);

				node.AppendChild(new_node);
				XMLUtils.SaveXML(doc, file);
			}
		}
		//增加一個ACL List
		public void AddAclList(string list_name, FSW_ACL action)
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "acl.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = string.Format("/configuration/network-lists/list[@name='{0}']", list_name);
			XmlNode node = null;
			XmlNode acl_list = doc.SelectSingleNode(xpath);

			if (null == acl_list)
			{
				xpath = string.Format("/configuration/network-lists");
				node = doc.SelectSingleNode(xpath);
				
				acl_list = doc.CreateNode(XmlNodeType.Element, "list", null);
				XmlAttribute attribute = doc.CreateAttribute("name");
				attribute.Value = list_name;
				acl_list.Attributes.Append(attribute);

				attribute = doc.CreateAttribute("default");
				attribute.Value = action.ToString();
				acl_list.Attributes.Append(attribute);

				node.AppendChild(acl_list);

				XMLUtils.SaveXML(doc, file);
			}
		}
		public void RemoveACL(string cidr_ip)
		{ RemoveACL(AclListName, cidr_ip); }
		public void RemoveACL(string list_name, string cidr_ip)
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "acl.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = string.Format("/configuration/network-lists/list[@name='{0}']/node[@cidr='{1}']", list_name, cidr_ip);
			XmlNode node = doc.SelectSingleNode(xpath);

			if (node != null)
			{
				node.ParentNode.RemoveChild(node);
				XMLUtils.SaveXML(doc, file);
			}
		}
		public void RemoveAclList(string list_name)
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "acl.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = string.Format("/configuration/network-lists/list[@name='{0}']", list_name);
			XmlNode node = doc.SelectSingleNode(xpath);

			if (node != null)
			{
				node.ParentNode.RemoveChild(node);
				XMLUtils.SaveXML(doc, file);
			}
		}

		public void SetEventSocketPwd(string pwd)
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "event_socket.conf.xml";
			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = string.Format("/configuration/settings/param[@name='password']");

			XmlNode node = doc.SelectSingleNode(xpath);

			if (node != null)
			{
				node.Attributes["value"].Value = pwd;
				XMLUtils.SaveXML(doc, file);
			}
		}

		//砍掉default name的ACLList再重建....
		public void ResetACL()
		{
			RemoveAclList(FSCtrl.AclListName);
			AddAclList(FSCtrl.AclListName, FSW_ACL.deny);
		}

		public void EnableEventSocket()
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "modules.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = "/configuration/modules//load[@module='mod_event_socket']";
			XmlNode node = doc.SelectSingleNode(xpath);

			if (node == null)
			{
				XmlNode new_node = doc.CreateNode(XmlNodeType.Element, "load", null);
				XmlAttribute attribute = doc.CreateAttribute("module");
				attribute.Value = "mod_event_socket";
				new_node.Attributes.Append(attribute);

				//找到正確的節點後把新創的node掛在它底下...
				xpath = "/configuration/modules";
				node = doc.SelectSingleNode(xpath);
				node.AppendChild(new_node);

				XMLUtils.SaveXML(doc, file);
			}
		}
		public void DisableEventSocket()
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "modules.conf.xml";

			XmlDocument doc = XMLUtils.LoadXML(file);
			string xpath = "/configuration/modules//load[@module='mod_event_socket']";
			XmlNode node = doc.SelectSingleNode(xpath);

			if (node != null)
			{
				node.ParentNode.RemoveChild(node);
				XMLUtils.SaveXML(doc, file);
			}
		}
		public void SetLogLevel(FSW_LOG_LEVEL level)
		{
			string path = GetServiceBinPath() + "\\conf\\autoload_configs";
			string file = path + "\\" + "switch.conf.xml";
			XmlDocument doc = XMLUtils.LoadXML(file);

			//XML的節點查詢用xpath語法....
			string xpath = "/configuration/settings//param[@name='loglevel']";
			XmlNode node = doc.SelectSingleNode(xpath);
			node.Attributes["value"].Value = level.ToString();

			XMLUtils.SaveXML(doc, file);
		}
	}
}
