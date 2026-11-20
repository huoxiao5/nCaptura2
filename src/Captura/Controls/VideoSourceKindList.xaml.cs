using Captura.Models;
using Captura.Video;
using Captura.ViewModels;
using Reactive.Bindings.Extensions;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Captura
{
    public partial class VideoSourceKindList
    {
        public VideoSourceKindList()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[VideoSourceKindList] Control loaded. ItemsSource type: {((ListView)this.Content).ItemsSource?.GetType().Name}");
                if (((ListView)this.Content).ItemsSource != null)
                {
                    var count = 0;
                    foreach (var item in ((ListView)this.Content).ItemsSource)
                    {
                        count++;
                        if (item is IVideoSourceProvider provider)
                        {
                            System.Diagnostics.Debug.WriteLine($"[VideoSourceKindList]   Item {count}: {provider.Name}, Icon={provider.Icon?.Substring(0, System.Math.Min(50, provider.Icon?.Length ?? 0))}");
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"[VideoSourceKindList] Total items in ItemsSource: {count}");
                }

                var vm = ServiceProvider.Get<RecordingViewModel>();

                // 监听状态：当录制结束（NotRecording）时触发
                vm.RecorderState
                  .ObserveProperty(x => x.Value)
                  .Subscribe(state =>
                  {
                      if (state == RecorderState.NotRecording)
                      {
                          Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                          {
                              // 1. UI 上取消所有选中高亮
                              if (this.Content is ListView listView)
                              {
                                  listView.SelectedItem = null;
                                  listView.UnselectAll();
                              }

                              // 2. 从底层集合中寻找鼠标模式 (AroundMouseSourceProvider)
                              // 你的源码中集合属性名叫 VideoSources
                              var mouseSource = vm.VideoSources.VideoSources.OfType<AroundMouseSourceProvider>().FirstOrDefault();

                              // 3. 如果找到了鼠标模式，就默默切过去（不触发 OnSelect，防弹框）
                              // 注意：因为直接赋值 SelectedVideoSourceKind 会走 ChangeSource(..., true)，导致 OnSelect 触发
                              // 我们直接利用 RestoreSourceKind，它走的是 ChangeSource(..., false)，不会弹区域框！
                              if (mouseSource != null)
                              {
                                  vm.VideoSources.RestoreSourceKind(mouseSource);
                              }
                              else
                              {
                                  // 如果没找到鼠标模式，就切回默认的全屏模式
                                  vm.VideoSources.SetDefaultSource();
                              }

                              // 4. 强行关闭并隐藏可能残留的 RegionSelector 窗口
                              for (int i = Application.Current.Windows.Count - 1; i >= 0; i--)
                              {
                                  var window = Application.Current.Windows[i];
                                  if (window is RegionSelector regionSelector)
                                  {
                                      regionSelector.Hide();
                                  }
                              }
                          }), DispatcherPriority.ApplicationIdle);
                      }
                  });
            };
        }

        void OnVideoSourceReSelect(object Sender, MouseButtonEventArgs E)
        {
            if (Sender is ListViewItem item && item.IsSelected)
            {
                if (item.DataContext is IVideoSourceProvider provider)
                {
                    provider.OnSelect();
                }
            }
        }
    }
}