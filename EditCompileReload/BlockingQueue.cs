namespace EditCompileReload;

internal class BlockingQueue<T>
{
    private readonly Queue<T> queue = new();

    public void Enqueue(T item)
    {
        lock (queue)
        {
            queue.Enqueue(item);
            if (queue.Count == 1)
            {
                // wake up any blocked dequeue
                Monitor.PulseAll(queue);
            }
        }
    }

    public T Dequeue()
    {
        lock (queue)
        {
            while (queue.Count == 0)
            {
                Monitor.Wait(queue);
            }
            var item = queue.Dequeue();
            return item;
        }
    }
}
