using Prism.Mvvm;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;
using System.ComponentModel;
using System.Reactive.Disposables;
using TCPServer;

namespace TCP_Server.ViewModels
{
	public class MainWindowViewModel : BindableBase, INotifyPropertyChanged
	{
		/// <summary>
		/// ウィンドウに表示するタイトル
		/// </summary>
		public ReactivePropertySlim<string> Title { get; } = new ReactivePropertySlim<string>("TCP Server");

		/// <summary>
		/// Disposeが必要な処理をまとめてやる
		/// </summary>
		private CompositeDisposable Disposable { get; } = new CompositeDisposable();

		/// <summary>
		/// MainWindowのCloseイベント
		/// </summary>
		public ReactiveCommand ClosedCommand { get; } = new ReactiveCommand();

		/// <summary>
		/// TCP Server オープン
		/// </summary>
		public ReactiveCommand OpenCommand { get; } = new ReactiveCommand();

		/// <summary>
		/// TCP Server クローズ
		/// </summary>
		public ReactiveCommand CloseCommand { get; } = new ReactiveCommand();

		/// <summary>
		/// 送受信するデータ
		/// </summary>
		public ReactivePropertySlim<string> TcpData { get; } = new ReactivePropertySlim<string>(string.Empty);

		/// <summary>
		/// これがTCPクライアントと接続するTCPサーバ
		/// </summary>
		private readonly Server server;

		/// <summary>
		/// コンストラクタ
		/// </summary>
		public MainWindowViewModel()
		{
			server = new Server(SendData);
			_ = ClosedCommand.Subscribe(Close).AddTo(Disposable);
			_ = OpenCommand.Subscribe(tcpOpne).AddTo(Disposable);
			_ = CloseCommand.Subscribe(tcpClose).AddTo(Disposable);

		}

		/// <summary>
		/// TCP通信を接続する
		/// </summary>
		private void tcpOpne()
		{
			server.Open("127.0.0.1", 50000, 2, 1024);
		}

		/// <summary>
		/// TCP通信を切断する
		/// </summary>
		private void tcpClose()
		{
			server.Close();
		}

		internal string SendData(string getString)
		{
			TcpData.Value = getString;
			return getString;
		}

		/// <summary>
		/// アプリが閉じられる時
		/// </summary>
		private void Close()
		{
			Disposable.Dispose();
		}
	}
}
