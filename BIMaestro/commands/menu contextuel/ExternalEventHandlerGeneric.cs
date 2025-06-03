using Autodesk.Revit.UI;

namespace TonNamespace
{
    public class ExternalEventHandlerGeneric : IExternalEventHandler
    {
        private PostableCommand _command;

        public ExternalEventHandlerGeneric(PostableCommand command)
        {
            _command = command;
        }

        public void Execute(UIApplication app)
        {
            app.PostCommand(RevitCommandId.LookupPostableCommandId(_command));
        }

        public string GetName()
        {
            return $"Déclencheur commande {_command}";
        }
    }
}
