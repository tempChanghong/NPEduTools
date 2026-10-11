using NPEduTools.Contracts;
namespace NPEduTools.Core;
public static class StudyExitNotice
{
    public const string Translations="Illegally circumventing oversight\n監督を違法に回避する\nContourner illégalement la supervision\nDie Aufsicht rechtswidrig umgehen\n非法规避监管\nEludir ilegalmente la supervisión\nEludere illegalmente la supervisione\n감독을 불법적으로 회피하다\nНезаконно обходить надзор\nContornar ilegalmente a supervisão";
    public static string Explanation(StudyExitState s)
    {
        string identity=s.Reason=="ProcessRecovery"?"自习程序曾异常中断，现已恢复定时监测。系统无法仅凭进程退出判断中断原因。":s.Reason=="WrongPassword"?"您输入的管理密码未通过验证，我们无法确认您的身份。":"您没能在规定的时间内通过管理验证，我们无法确认您的身份。";
        string photo=s.PhotoState switch {
            "Stored"=>"我们已经自动捕获了摄像头画面，并将该画面安全上传至NPClassworksKV服务器。",
            "Captured"=>"本次摄像头画面已捕获并在本机加密保存，正在等待上传至NPClassworksKV服务器。",
            "Capturing"=>"正在尝试捕获本次摄像头画面，目前尚未确认捕获或上传成功。",
            "Suppressed"=>"本次未取得已确认的摄像头画面，可能因拍照间隔或当前照片策略未触发。",
            "Failed"=>"本次摄像头捕获或上传失败，没有已确认上传的画面。",
            _=>"本次摄像头画面的捕获与上传状态尚无法确认。"};
        string audit=s.AuditState switch {
            "Stored"=>s.PhotoState=="Stored"?"本次退出验证未通过的记录已与本次已存储画面一同上传。":"本次退出验证未通过的记录已上传。摄像头画面状态以上述实际结果为准。",
            "SavedLocal"=>"本次退出验证未通过已在本机加密记录，正在等待上传。",
            _=>"本次退出验证未通过，但记录保存或上传尚未确认。"};
        if(s.Reason=="ProcessRecovery")audit=audit.Replace("退出验证未通过","异常中断与恢复");
        return identity+"\n\n"+photo+"\n\n仅同校学校管理后台的 OWNER 与第二管理员可查看已存储画面。我们不允许任何人员将涉及公民肖像权的摄像头捕获画面上传至公开互联网。\n\n请不要再做出任何尝试绕过自习监管的行为。"+audit;
    }
}
