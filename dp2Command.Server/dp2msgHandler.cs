﻿using DigitalPlatform;
using DigitalPlatform.Message;
using DigitalPlatform.MessageClient;
using DigitalPlatform.Xml;
using Senparc.Weixin.MP.AdvancedAPIs;
using Senparc.Weixin.MP.AdvancedAPIs.TemplateMessage;
using Senparc.Weixin.MP.CommonAPIs;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace dp2Command.Service
{
    public class dp2MsgHandler
    {
        MessageConnectionCollection _channels = null;
        private string _dp2mserverUrl = "";
        private string _weiXinAppId = "";
        

        // 轮循线程
        WxMsgThread _wxMsgThread = null;

        public dp2MsgHandler(MessageConnectionCollection channels,
            string dp2mserverUrl,
            string weiXinAppId)
        {
            this._channels = channels;
            this._dp2mserverUrl = dp2mserverUrl;
            this._weiXinAppId = weiXinAppId;

            // 接管消息事件
            this._channels.AddMessage += _channels_AddMessage;

            // 启一个轮循线程获取消息
            this._wxMsgThread = new WxMsgThread();
            this._wxMsgThread.Container = this;
            this._wxMsgThread.BeginThread();    // TODO: 应该在 MessagWorkereConnection 第一次连接成功以后，再启动这个线程比较好
 
        }

        /// <summary>
        /// 消息事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void _channels_AddMessage(object sender, AddMessageEventArgs e)
        {
            if (e.Action != "create")
            {
                return;
            }

            if (e.Records != null)
            {
                DoMessage(e.Records);
            }
        }

        // 被1分钟轮循一次的工作线程调用
        public async void DoLoadMessage()
        {
            string strGroupName = "_patronNotify";//"<default>";

            string strError = "";
            var accessToken = AccessTokenContainer.GetAccessToken(this._weiXinAppId);

            CancellationToken cancel_token = new CancellationToken();
            string id = Guid.NewGuid().ToString();
            GetMessageRequest request = new GetMessageRequest(id,
                "",
                strGroupName, // "" 表示默认群组
                "",
                "",
                0,
                -1);
            try
            {
                MessageConnection connection = await this._channels.GetConnectionAsync(
                    this._dp2mserverUrl,
                    "");  //todo 这里用空可以吗？
                MessageResult result = await connection.GetMessageAsync(
                        request,
                        OutputMessage,
                        new TimeSpan(0, 1, 0),
                        cancel_token);
            }
            catch (AggregateException ex)
            {
                strError = MessageConnection.GetExceptionText(ex);
                goto ERROR1;
            }
            catch (Exception ex)
            {
                strError = ex.Message;
                goto ERROR1;
            }
            return;

        ERROR1:
            dp2CmdService2.Instance.WriteErrorLog(strError);
        }

        void OutputMessage(long totalCount,
            long start,
            IList<MessageRecord> records,
            string errorInfo,
            string errorCode)
        {
            if (totalCount == -1) // todo 什么情况下-1
            {
                StringBuilder text = new StringBuilder();
                text.Append("***\r\n");
                text.Append("totalCount=" + totalCount + "\r\n");
                text.Append("errorInfo=" + errorInfo + "\r\n");
                text.Append("errorCode=" + errorCode + "\r\n");
                dp2CmdService2.Instance.WriteErrorLog(text.ToString());
                return;
            }

            if (records != null && records.Count > 0)
            {
                DoMessage(records);
            }
        }

        //已处理过的消息队列
        Hashtable msgHashTable = new Hashtable();
        private bool checkMsgIsDone(string msgId)
        {
            if (msgHashTable.ContainsKey(msgId))
                return true;

            return false;
        }

        private bool AddMsgToHashTable(string msgId)
        {
            if (this.msgHashTable.ContainsKey(msgId))
                return false;

            this.msgHashTable[msgId] = DateTime.Now;
            return true;
        }

        // 处理消息锁
        private object msgLocker = new object();

        /// <summary>
        /// 实际处理通知消息
        /// </summary>
        /// <param name="records"></param>
        private void DoMessage(IList<MessageRecord> records)
        {
            // getMessage与addMessage得到消息，都会走到处理消息的里，加锁让2个线程排队
            lock (msgLocker)
            {
                try
                {
                    if (records == null || records.Count == 0)
                        return;

                    List<string> delIds = new List<string>();
                    foreach (MessageRecord record in records)
                    {
                        // 先检查一下是不是_patronNotify组消息，因为addMessage会得到账户配置的所有组的消息，getMessage没关系只获取_patronNotify群消息
                        bool bPatronNotifyGroup = this.CheckIsNotifyGroup(record.groups);
                        if (bPatronNotifyGroup == false)
                        {
                            continue;
                        }

                        // 从已处理消息队列里查重，如果是前面处理过的，则不再处理
                        bool bSended = this.checkMsgIsDone(record.id);
                        if (bSended == true)
                            continue;

                        string strError="";
                        /// <returns>
                        /// -1 不符合条件，不处理
                        /// 0 未绑定微信id，未处理
                        /// 1 成功
                        /// </returns>
                        int nRet = this.InternalDoMessage(record, out strError);
                        if (nRet == -1)
                        {
                            dp2CmdService2.Instance.WriteErrorLog(strError);
                            // todo,对于这些消息是否删除？，现在是统统删除
                        }


                        // 加到已处理消息队列里
                        this.AddMsgToHashTable(record.id);

                        // 加到删除列表
                        delIds.Add(record.id);
                    }

                    //删除处理过的消息
                    if (delIds.Count > 0)
                    {
                        string strError = "";
                        int nRet = this.DeleteMessage(delIds, out strError);
                        if (nRet == -1)
                            throw new Exception(strError);
                    }
                }
                catch (Exception ex)
                {
                    dp2CmdService2.Instance.WriteErrorLog(ex.Message);
                }
            }
        }

        /// <summary>
        /// 内部处理消息
        /// </summary>
        /// <param name="record"></param>
        /// <param name="strError"></param>
        /// <returns>
        /// -1 不符合条件，不处理
        /// 0 未绑定微信id，未处理
        /// 1 成功
        /// </returns>
        private int InternalDoMessage(MessageRecord record,out string strError)
        {
            strError = "";

            string id = record.id;
            string data = record.data;
            string[] group = record.groups;
            string create = record.creator;


            //<root>
            //    <type>patronNotify</type>
            //    <recipient>R0000001@LUID:62637a12-1965-4876-af3a-fc1d3009af8a</recipient>
            //    <mime>xml</mime>
            //    <body>...</body>
            //</root>
            XmlDocument dataDom = new XmlDocument();
            try
            {
                dataDom.LoadXml(data);
            }
            catch (Exception ex)
            {
                strError="加载消息返回的data到xml出错:" + ex.Message;
                return -1;
            }

            XmlNode nodeType = dataDom.DocumentElement.SelectSingleNode("type");
            if (nodeType == null)
            {
                strError = "尚未定义<type>节点";
                return -1;
            }
            string type = DomUtil.GetNodeText(nodeType);
            if (type != "patronNotify") //只处理通知消息
            {
                strError = "<type>节点值不是patronNotify。";
                return -1;
            }

            XmlNode nodeBody = dataDom.DocumentElement.SelectSingleNode("body");
            if (nodeBody == null)
            {
                strError="data中不存在body节点";
                return -1;
            }

            /*
            body元素里面是预约到书通知记录(注意这是一个字符串，需要另行装入一个XmlDocument解析)，其格式如下：
            <?xml version="1.0" encoding="utf-8"?>
            <root>
                <type>预约到书通知</type>
                <itemBarcode>0000001</itemBarcode>
                <refID> </refID>
                <opacURL>/book.aspx?barcode=0000001</opacURL>
                <reserveTime>2天</reserveTime>
                <today>2016/5/17 10:10:59</today>
                <summary>船舶柴油机 / 聂云超主编. -- ISBN 7-...</summary>
                <patronName>张三</patronName>
                <patronRecord>
                    <barcode>R0000001</barcode>
                    <readerType>本科生</readerType>
                    <name>张三</name>
                    <refID>be13ecc5-6a9c-4400-9453-a072c50cede1</refID>
                    <department>数学系</department>
                    <address>address</address>
                    <cardNumber>C12345</cardNumber>
                    <refid>8aa41a9a-fb42-48c0-b9b9-9d6656dbeb76</refid>
                    <email>email:xietao@dp2003.com,weixinid:testwx2</email>
                    <tel>13641016400</tel>
                    <idCardNumber>1234567890123</idCardNumber>
                </patronRecord>
            </root
            */
            XmlDocument bodyDom = new XmlDocument();
            try
            {
                bodyDom.LoadXml(nodeBody.InnerText);//.InnerXml); 
            }
            catch (Exception ex)
            {
                strError="加载消息data中的body到xml出错:" + ex.Message;
                return -1;
            }
            XmlNode root = bodyDom.DocumentElement;
            XmlNode typeNode = root.SelectSingleNode("type");
            if (typeNode == null)
            {
                strError = "消息data的body中未定义type节点。";
                return -1;
            }
            string strType = DomUtil.GetNodeText(typeNode);

            // 目前只处理这两种消息
            if (strType != "预约到书通知" && strType != "超期通知")
            {
                strError = "不是预约或超期通知类消息";
                return -1;
            }

            int nRet = 0;

            // 根据类型发送不同的模板消息
            if (strType == "预约到书通知")
            {
                nRet =this.SendArrived(bodyDom,out strError);
            }
            else if (strType == "超期通知")
            {
                nRet=this.SendCaoQi(bodyDom,out strError);
            }

            return nRet;
        }
        /// <summary>
        /// 检查是否属于通知组的消息
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        private bool CheckIsNotifyGroup(string[] arrayGroup)
        {
            foreach (string s in arrayGroup)
            {
                if (s == "_patronNotify" || s == "gn:_patronNotify")
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 删除消息
        /// </summary>
        /// <param name="idList"></param>
        /// <param name="strError"></param>
        /// <returns></returns>
        int DeleteMessage(List<string> idList, out string strError)
        {
            strError = "";
            if (idList.Count == 0)
                return 0;

            string strGroupName = "gn:_patronNotify";

            List<MessageRecord> records = new List<MessageRecord>();
            for (int i = 0; i < idList.Count; i++)
            {
                if (idList[i].Trim() == "")
                    continue;

                MessageRecord record = new MessageRecord();
                record.groups = strGroupName.Split(new char[] { ',' });
                record.id = idList[i].Trim();
                records.Add(record);
            }

            SetMessageRequest param = new SetMessageRequest("delete",
                "",
                records);

            try
            {
                MessageConnection connection = this._channels.GetConnectionAsync(
                    this._dp2mserverUrl,
                    "").Result;

                SetMessageResult result = connection.SetMessageAsync(param).Result;
                if (result.Value == -1)
                {
                    strError = result.ErrorInfo;
                    return -1;
                }

                return 0;
            }
            catch (AggregateException ex)
            {
                strError = MessageConnection.GetExceptionText(ex);
                goto ERROR1;
            }
            catch (Exception ex)
            {
                strError = ex.Message;
                goto ERROR1;
            }

        ERROR1:
            return -1;
        }


        /// <summary>
        /// 发送预约通知
        /// </summary>
        /// <param name="bodyDom"></param>
        /// <param name="strError"></param>
        /// <returns>
        /// -1 出错，格式出错或者发送模板消息出错
        /// 0 未绑定微信id
        /// 1 成功
        /// </returns>
        public int SendArrived(XmlDocument bodyDom,out string strError)
        {
            strError = "";



            /*
           body元素里面是预约到书通知记录(注意这是一个字符串，需要另行装入一个XmlDocument解析)，其格式如下：
           <?xml version="1.0" encoding="utf-8"?>
           <root>
               <type>预约到书通知</type>
               <itemBarcode>0000001</itemBarcode>
               <refID> </refID>
               <opacURL>/book.aspx?barcode=0000001</opacURL>
               <reserveTime>2天</reserveTime>
               <today>2016/5/17 10:10:59</today>
               <summary>船舶柴油机 / 聂云超主编. -- ISBN 7-...</summary>
               <patronName>张三</patronName>
               <patronRecord>
                   <barcode>R0000001</barcode>
                   <readerType>本科生</readerType>
                   <name>张三</name>
                   <refID>be13ecc5-6a9c-4400-9453-a072c50cede1</refID>
                   <department>数学系</department>
                   <address>address</address>
                   <cardNumber>C12345</cardNumber>
                   <refid>8aa41a9a-fb42-48c0-b9b9-9d6656dbeb76</refid>
                   <email>email:xietao@dp2003.com,weixinid:testwx2</email>
                   <tel>13641016400</tel>
                   <idCardNumber>1234567890123</idCardNumber>
               </patronRecord>
           </root
           */

            string patronName = "";
            List<string> weiXinIdList = this.GetWeiXinIds(bodyDom, out patronName);
            if (weiXinIdList.Count == 0)
            {
                strError = "未绑定微信id";
                return 0;
            }

            XmlNode root = bodyDom.DocumentElement;
            // <reserveTime>2天</reserveTime>
            // <today>2016/5/17 10:10:59</today>
            // 取出预约消息
            XmlNode nodeSummary = root.SelectSingleNode("summary");
            if (nodeSummary == null)
            {
                strError="尚未定义<summary>节点";
                return -1;
            }
            string summary = DomUtil.GetNodeText(nodeSummary);

            XmlNode nodeReserveTime = root.SelectSingleNode("reserveTime");
            if (nodeReserveTime == null)
            {
                strError = "尚未定义<reserveTime>节点";
                return -1;
            }
            string reserveTime = DomUtil.GetNodeText(nodeReserveTime);

            XmlNode nodeToday = root.SelectSingleNode("today");
            if (nodeToday == null)
            {
                strError = "尚未定义<today>节点";
                return -1;
            }
            string today = DomUtil.GetNodeText(nodeToday);


            foreach (string weiXinId in weiXinIdList)
            {
                var accessToken = AccessTokenContainer.GetAccessToken(this._weiXinAppId);

                //{{first.DATA}}
                //图书书名：{{keyword1.DATA}}
                //到书日期：{{keyword2.DATA}}
                //保留期限：{{keyword3.DATA}}
                //{{remark.DATA}}
                var msgData = new ArrivedTemplateData()
                {
                    first = new TemplateDataItem("尊敬的读者：您预约的图书已经到书，请尽快来图书馆办理借书手续。", "#000000"),
                    keyword1 = new TemplateDataItem(summary, "#000000"),//text.ToString()),// "请让我慢慢长大"),
                    keyword2 = new TemplateDataItem(today, "#000000"),
                    keyword3 = new TemplateDataItem("保留" + reserveTime, "#000000"),
                    remark = new TemplateDataItem("\n如果您未能在保留期限内来馆办理借阅手续，图书馆将把优先借阅权转给后面排队等待的预约者，或做归架处理。", "#CCCCCC")
                };

                // 发送预约模板消息
                //string detailUrl = "https://open.weixin.qq.com/connect/oauth2/authorize?appid=wx57aa3682c59d16c2&redirect_uri=http%3a%2f%2fdp2003.com%2fdp2weixin%2fPatron%2fIndex&response_type=code&scope=snsapi_base&state=dp2weixin#wechat_redirect";
                var result1 = TemplateApi.SendTemplateMessage(accessToken,
                    weiXinId,
                    dp2CmdService2.C_Template_Arrived,
                    "#FF0000",
                    "",//不出现详细了
                    msgData);
                if (result1.errcode != 0)
                {
                    strError = result1.errmsg;
                    return -1;
                }
            }


            return 1;
        }

        /// <summary>
        /// 发送超期通知
        /// </summary>
        /// <param name="bodyDom"></param>
        public int SendCaoQi(XmlDocument bodyDom,out string strError)
        {
            strError = "";

            /*
<root>
    <type>超期通知</type>
    <items overdueCount="1" normalCount="0">
        <item summary="船舶柴油机 / 聂云超主编. -- ISBN 7-..." timeReturning="2016/5/18" overdue="已超期 31 天" overdueType="overdue" />
    </items>
    <text>您借阅的下列书刊：
船舶柴油机 / 聂云超主编. -- ISBN 7-... 应还日期: 2016/5/18 已超期 31 天
</text>
    <patronRecord>...
    </patronRecord>
</root>

           */

            // 得到绑定的微信id
            string patronName = "";
            List<string> weiXinIdList = this.GetWeiXinIds(bodyDom, out patronName);
            if (weiXinIdList.Count == 0)
            {
                strError = "未绑定微信id";
                return 0;
            }

            XmlNode root = bodyDom.DocumentElement;

            // 取出册列表
            XmlNode nodeItems = root.SelectSingleNode("items");
            string overdueCount = DomUtil.GetAttr(nodeItems, "overdueCount");

            XmlNodeList nodeList = nodeItems.SelectNodes("item");
            // 一册一个通知
            foreach (XmlNode item in nodeList)
            {
                string summary = DomUtil.GetAttr(item, "summary");
                string timeReturning = DomUtil.GetAttr(item, "timeReturning");
                string overdue = DomUtil.GetAttr(item, "overdue");


                foreach (string weiXinId in weiXinIdList)
                {
                    var accessToken = AccessTokenContainer.GetAccessToken(this._weiXinAppId);

                    //{{first.DATA}}
                    //图书书名：{{keyword1.DATA}}
                    //应还日期：{{keyword2.DATA}}
                    //超期天数：{{keyword3.DATA}}
                    //{{remark.DATA}}
                    var msgData = new ArrivedTemplateData()
                    {
                        first = new TemplateDataItem("尊敬的" + patronName + " 您好！您借阅的图书已超期，请尽快归还。", "#000000"),
                        keyword1 = new TemplateDataItem(summary, "#000000"),//text.ToString()),// "请让我慢慢长大"),
                        keyword2 = new TemplateDataItem(timeReturning, "#000000"),
                        keyword3 = new TemplateDataItem(overdue, "#000000"),
                        remark = new TemplateDataItem("\n点击下方”详情“查看个人详细信息。", "#CCCCCC")
                    };

                    string detailUrl = "https://open.weixin.qq.com/connect/oauth2/authorize?appid=wx57aa3682c59d16c2&redirect_uri=http%3a%2f%2fdp2003.com%2fdp2weixin%2fPatron%2fIndex&response_type=code&scope=snsapi_base&state=dp2weixin#wechat_redirect";
                    var result1 = TemplateApi.SendTemplateMessage(accessToken,
                        weiXinId,
                        dp2CmdService2.C_Template_CaoQi,
                        "#FF0000",
                        detailUrl,//不出现详细了
                        msgData);
                    if (result1.errcode != 0)
                    {
                        strError = result1.errmsg;
                        return -1;
                    }
                }
            }

            // 发送成功
            return 1;
        }

        /// <summary>
        /// 获取读者记录中绑定的微信id,返回数组
        /// </summary>
        /// <param name="bodyDom"></param>
        /// <param name="patronName"></param>
        /// <returns></returns>
        private List<string> GetWeiXinIds(XmlDocument bodyDom, out string patronName)
        {
            patronName = "";

            XmlNode root = bodyDom.DocumentElement;
            XmlNode patronRecordNode = root.SelectSingleNode("patronRecord");
            if (patronRecordNode == null)
                throw new Exception("尚未定义<patronRecordNode>节点");
            patronName = DomUtil.GetNodeText(patronRecordNode.SelectSingleNode("name"));
            XmlNode emailNode = patronRecordNode.SelectSingleNode("email");
            if (emailNode == null)
                throw new Exception("尚未定义<email>节点");
            string email = DomUtil.GetNodeText(emailNode);
            //<email>test@163.com,123,weixinid:o4xvUviTxj2HbRqbQb9W2nMl4fGg,weixinid:o4xvUvnLTg6NnflbYdcS-sxJCGFo,weixinid:testid</email>
            string[] emailList = email.Split(new char[] { ',' });
            List<string> weiXinIdList = new List<string>();
            for (int i = 0; i < emailList.Length; i++)
            {
                string oneEmail = emailList[i].Trim();
                if (oneEmail.Length > 9 && oneEmail.Substring(0, 9) == dp2CommandUtility.C_WeiXinIdPrefix)
                {
                    string weiwinId = oneEmail.Substring(9).Trim();
                    if (weiwinId != "")
                        weiXinIdList.Add(weiwinId);
                }
            }
            return weiXinIdList;
        }

    }

    /// <summary>
    /// 用于搜集 dp2library 通知消息并发送给 dp2mserver 的线程
    /// </summary>
    public class WxMsgThread : ThreadBase
    {
        public dp2MsgHandler Container = null;

        public WxMsgThread()
        {
            this.PerTime = 60 * 1000;
        }

        // 工作线程每一轮循环的实质性工作
        public override void Worker()
        {
            if (this.Stopped == true)
                return;

            Container.DoLoadMessage();
        }
    }

}
