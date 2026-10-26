using System.Reflection;

namespace LPS.APS.Core.Extensions;

/// <summary>
/// Scrutor 自动扫描的**共用过滤谓词**。
///
/// 扫描一律以 <c>AsImplementedInterfaces()</c> 注册，而有三类类型「天生带接口」，会被误当服务：
///   - **record** 天生实现 <c>IEquatable&lt;T&gt;</c>，注册后 <c>ValidateOnBuild</c> 构造时
///     因参数（如 <c>string</c>／<c>bool</c>／<c>DateTime</c>）无法解析而**启动即失败**；
///   - **嵌套类型**是对外层类型的实现细节，不应被当独立服务拖进容器；
///   - **无公开构造函数**的类型（如「只能走静态工厂 <c>Start()</c> 建、构造器私有」的观察器）
///     被 <c>AsImplementedInterfaces()</c> 按它实现的接口（<c>IAsyncDisposable</c> 等）注册，
///     <c>ValidateOnBuild</c> 报「A suitable constructor ... could not be located」，同样**启动即失败**。
///
/// 已发生三次：2026-10-13 <c>FenceParameters／FenceResolution／AnchorDiagnostic</c>、
/// 2026-10-17 <c>BomNightBatchResolver.PushRecord／Resolution</c>（均在
/// <c>LPS.APS.Engine.Services.Sync</c> 下）、2026-10-26 <c>ComputeStageProgressMonitor</c>
/// （<c>LPS.APS.Application.Services</c>，私有构造 ＋ <c>IAsyncDisposable</c>）。
/// 谓词放在这里，两个扫描点共用一份，避免再各写一套而漏掉其中一处。
/// </summary>
public static class ServiceScanFilters
{
    /// <summary>
    /// record 判定：编译器为 record class 生成非公开的 <c>EqualityContract</c> 属性。
    /// record struct 无需判定 —— Scrutor <c>AddClasses</c> 默认按 <c>IsClass &amp;&amp; !IsAbstract</c> 过滤，值类型本就进不来。
    /// </summary>
    public static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) != null;

    /// <summary>
    /// 是否有 DI 可激活的公开构造函数。<c>GetConstructors()</c> 只返回 public 实例构造器。
    /// 构造器私有的类型（如只能经静态工厂 <c>Start()</c> 创建、且实现 <c>IAsyncDisposable</c> 的观察器）
    /// 一旦被扫描注册，<c>ValidateOnBuild</c> 立刻报「A suitable constructor ... could not be located」。
    /// </summary>
    public static bool HasPublicConstructor(Type type) => type.GetConstructors().Length > 0;

    /// <summary>可参与 Scrutor 扫描的类型：非嵌套、非异常、非 record、有公开构造函数。</summary>
    public static bool IsServiceCandidate(Type type)
        => type.DeclaringType == null
           && !typeof(Exception).IsAssignableFrom(type)
           && !IsRecord(type)
           && HasPublicConstructor(type);
}
