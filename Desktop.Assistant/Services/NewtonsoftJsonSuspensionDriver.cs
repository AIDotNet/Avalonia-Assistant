using Desktop.Assistant.ViewModels;
using Newtonsoft.Json;
using ReactiveUI;
using Splat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Services
{
    public class NewtonsoftJsonSuspensionDriver : ISuspensionDriver
    {
        private readonly string _file;
        private readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All
        };

        public NewtonsoftJsonSuspensionDriver(string file) => _file = file;

        public IObservable<Unit> InvalidateState()
        {
            if (File.Exists(_file))
                File.Delete(_file);
            return Observable.Return(Unit.Default);
        }

        public IObservable<object> LoadState()
        {
            if (!File.Exists(_file))
            {
                var obj = Locator.Current.GetService<IScreen>();
                //string obj = "{\"$type\":\"Desktop.Assistant.ViewModels.MainWindowViewModel, Desktop.Assistant\"}";
                SaveState(obj);
            }

            var lines = File.ReadAllText(_file);
            var state = JsonConvert.DeserializeObject<object>(lines, _settings);
            return Observable.Return(state);
        }

        public IObservable<Unit> SaveState(object state)
        {
            //var lines = JsonConvert.SerializeObject(state, _settings);
            //File.WriteAllText(_file, lines);
            return Observable.Return(Unit.Default);
        }
    }
}
