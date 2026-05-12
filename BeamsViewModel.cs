using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TNovBeams
{
    public class BeamsViewModel : INotifyPropertyChanged
    {
        private bool _all = true;
        public bool all
        {
            get => _all; set { _all = value; OnPropertyChanged(); }
        }
        private bool _visible = false;
        public bool visible
        {
            get => _visible; set { _visible = value; OnPropertyChanged(); }
        }
        private bool _selected = false;
        public bool selected
        {
            get => _selected; set { _selected = value; OnPropertyChanged(); }
        }
        private bool _cut = true;
        public bool cut
        {
            get => _cut; set { _cut = value; OnPropertyChanged(); }
        }
        private bool _pars = true;
        public bool pars
        {
            get => _pars; set { _pars = value; OnPropertyChanged(); }
        }

        public event EventHandler CloseRequest;
        private void RaiseCloseRequest()
        {
            CloseRequest?.Invoke(this, EventArgs.Empty);
        }
        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string PropertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }

    }
}
