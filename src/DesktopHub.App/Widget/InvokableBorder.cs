using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace DesktopHub.App.Widget;

/// <summary>
/// Border que tecnologias assistivas (Narrador, UI Automation) enxergam como um botão acionável.
/// A ilha não tem botão visível de expandir — o clique na própria ilha expande — então é este
/// peer que torna a ação disponível sem mouse.
/// </summary>
public sealed class InvokableBorder : Border
{
    public event EventHandler? Invoked;

    protected override AutomationPeer OnCreateAutomationPeer() => new InvokablePeer(this);

    private void RaiseInvoked() => Invoked?.Invoke(this, EventArgs.Empty);

    private sealed class InvokablePeer(InvokableBorder owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(InvokableBorder);

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke() => owner.RaiseInvoked();
    }
}
