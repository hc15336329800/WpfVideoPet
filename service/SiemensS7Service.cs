using HandyControl.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;



//Siemens 的 “S7 通讯协议”
namespace WpfVideoPet.service
{
    public class SiemensS7Service
    {
        //todo: 1、启动后后台一直独立运行（不影响其他的UI等线程）  2、一直3s轮询db100的3个字节（开关量）输出到控制台一份  3、提供可以单独写入修改db200 （开关量） 4、点位对接  可以参考西门子PLC1200的DB点位对接文档.xlsx
        //todo: 5、plc地址等配置放入webrtcsettings.json   6、轮询的数据要通过mqtt传送给对应主题（先随便定义个777）  7、mqtt接收到主题（先随便定义个888）后解析数据进行对plc点位的修改。   8、模块化简单化不要太粘连并且减少大的改动 ，保持稳定。

    }
}
